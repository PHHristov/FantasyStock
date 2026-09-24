"use strict";

// Where the backend lives. Override by defining window.FANTASYSTOCK_API_BASE
// before this script loads (e.g. when the backend isn't on localhost:8080).
const API_BASE = window.FANTASYSTOCK_API_BASE || "http://localhost:8080";
const WS_URL = API_BASE.replace(/^http/, "ws") + "/ws/prices";
const SESSION_KEY = "fantasystock.session";
// Every account starts with this much cash (see db/02_app_schema.sql and
// AuthService.RegisterAsync) - used to show gain/loss since sign-up.
const STARTING_CASH = 100000;

const money = new Intl.NumberFormat("en-US", { style: "currency", currency: "USD" });
const shares = new Intl.NumberFormat("en-US", { maximumFractionDigits: 6 });
const volumeFmt = new Intl.NumberFormat("en-US", { notation: "compact", maximumFractionDigits: 1 });
const pct = new Intl.NumberFormat("en-US", {
  style: "percent", minimumFractionDigits: 2, maximumFractionDigits: 2, signDisplay: "exceptZero",
});
const timeFmt = new Intl.DateTimeFormat(undefined, { dateStyle: "short", timeStyle: "medium" });

const state = {
  session: null,          // { token, userId, username }
  latest: new Map(),      // ticker -> latest tick (as sent by the backend)
  history: new Map(),     // ticker -> Map(tradeDate -> close)
  selected: null,         // ticker shown in the chart / trade ticket
  side: "BUY",
  authMode: "login",      // "login" | "register"
  portfolio: null,        // last GET /me/portfolio response
  trades: [],             // last GET /me/trades response
};

const $ = (id) => document.getElementById(id);

// ---------------------------------------------------------------- helpers

function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (value == null || value === false) continue;
    if (key === "class") node.className = value;
    else if (key.startsWith("on")) node.addEventListener(key.slice(2), value);
    else node.setAttribute(key, value === true ? "" : value);
  }
  for (const child of children) {
    if (child != null) node.append(child);
  }
  return node;
}

function showMessage(node, text, kind) {
  if (!text) {
    node.hidden = true;
    node.textContent = "";
    return;
  }
  node.textContent = text;
  node.className = `message ${kind}`;
  node.hidden = false;
}

function setChange(node, ratio) {
  node.classList.remove("up", "down", "flat");
  if (ratio == null || !Number.isFinite(ratio)) {
    node.textContent = "";
    return;
  }
  node.textContent = pct.format(ratio);
  node.classList.add(ratio > 0 ? "up" : ratio < 0 ? "down" : "flat");
}

function cssVar(name) {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

// ---------------------------------------------------------------- session

function loadSession() {
  try {
    const raw = sessionStorage.getItem(SESSION_KEY);
    return raw ? JSON.parse(raw) : null;
  } catch {
    return null;
  }
}

function saveSession(session) {
  try {
    if (session) sessionStorage.setItem(SESSION_KEY, JSON.stringify(session));
    else sessionStorage.removeItem(SESSION_KEY);
  } catch {
    // storage unavailable (private mode etc.) - the session just won't survive a reload
  }
}

// The backend's JWTs carry an `exp` claim; drop a stored token that's
// already expired instead of letting the first API call fail with a 401.
function tokenExpired(token) {
  try {
    const payload = JSON.parse(atob(token.split(".")[1].replace(/-/g, "+").replace(/_/g, "/")));
    return typeof payload.exp !== "number" || payload.exp * 1000 <= Date.now();
  } catch {
    return true;
  }
}

// ---------------------------------------------------------------- API

async function api(path, { method = "GET", body, auth = false } = {}) {
  const headers = {};
  if (body !== undefined) headers["Content-Type"] = "application/json";
  if (auth && state.session) headers["Authorization"] = `Bearer ${state.session.token}`;

  let response;
  try {
    response = await fetch(API_BASE + path, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch {
    throw new Error(`Can't reach the backend at ${API_BASE}. Is it running?`);
  }

  const text = await response.text();
  let data = null;
  try {
    data = text ? JSON.parse(text) : null;
  } catch {
    data = null;
  }

  if (response.status === 401 && auth) {
    logout("Your session expired. Log in again.");
    throw new Error("Your session expired. Log in again.");
  }
  if (!response.ok) {
    throw new Error(data?.error || `Request failed (${response.status}).`);
  }
  return data;
}

// ---------------------------------------------------------------- market data

function ingestTick(tick) {
  if (!tick || !tick.ticker) return;
  const ticker = tick.ticker.toUpperCase();
  state.latest.set(ticker, tick);

  if (!state.history.has(ticker)) state.history.set(ticker, new Map());
  // Keyed by simulated trade date: when the tick-engine loops back to the
  // start of its history, the same dates just overwrite themselves.
  state.history.get(ticker).set(tick.tradeDate, Number(tick.close));

  if (!state.selected) state.selected = ticker;
}

function sortedHistory(ticker) {
  const series = state.history.get(ticker);
  if (!series) return [];
  return [...series.entries()].sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0));
}

// Day-over-day change: this tick's close vs. the previous simulated trading
// day's close, if we've seen it.
function dayChange(ticker) {
  const tick = state.latest.get(ticker);
  if (!tick) return null;
  const series = sortedHistory(ticker);
  const index = series.findIndex(([date]) => date === tick.tradeDate);
  if (index <= 0) return null;
  const previous = series[index - 1][1];
  return (Number(tick.close) - previous) / previous;
}

function lastPrice(ticker) {
  const tick = state.latest.get(ticker);
  return tick ? Number(tick.close) : null;
}

async function seedPrices() {
  try {
    const ticks = await api("/prices");
    for (const tick of ticks ?? []) ingestTick(tick);
    scheduleRender();
  } catch {
    // backend not up yet - the WebSocket reconnect loop will catch up later
  }
}

let socket = null;
let reconnectAttempt = 0;

function setFeedStatus(status) {
  const node = $("feed-status");
  node.dataset.state = status;
  node.querySelector(".feed-label").textContent =
    status === "live" ? "Live" : status === "offline" ? "Reconnecting…" : "Connecting";
}

function connectFeed() {
  setFeedStatus(reconnectAttempt === 0 ? "connecting" : "offline");
  try {
    socket = new WebSocket(WS_URL);
  } catch {
    scheduleReconnect();
    return;
  }

  socket.addEventListener("open", () => {
    reconnectAttempt = 0;
    setFeedStatus("live");
    seedPrices();
  });
  socket.addEventListener("message", (event) => {
    try {
      ingestTick(JSON.parse(event.data));
      scheduleRender();
    } catch {
      // ignore a malformed frame rather than killing the feed
    }
  });
  socket.addEventListener("close", () => {
    setFeedStatus("offline");
    scheduleReconnect();
  });
}

function scheduleReconnect() {
  const delay = Math.min(15000, 1000 * 2 ** reconnectAttempt);
  reconnectAttempt += 1;
  setTimeout(connectFeed, delay);
}

// ---------------------------------------------------------------- rendering

let renderQueued = false;

// Ticks arrive in bursts (one message per ticker per simulated day) - batch
// them into a single render. A short timer rather than requestAnimationFrame,
// which stops firing in background tabs and would freeze the page there.
function scheduleRender() {
  if (renderQueued) return;
  renderQueued = true;
  setTimeout(() => {
    renderQueued = false;
    renderSimDate();
    renderMarket();
    renderChart();
    renderTicket();
    renderPortfolio();
  });
}

function renderSimDate() {
  const node = $("sim-date");
  const any = state.latest.values().next().value;
  if (!any) {
    node.textContent = "Waiting for market data…";
    return;
  }
  node.replaceChildren(
    "Simulated market day ",
    el("strong", { class: "num" }, any.tradeDate),
    ` · replay lap ${any.lap}`,
  );
}

function renderMarket() {
  const tickers = [...state.latest.keys()].sort();
  $("market-empty").hidden = tickers.length > 0;

  const items = tickers.map((ticker) => {
    const change = el("span", { class: "c change num" });
    setChange(change, dayChange(ticker));
    const button = el(
      "button",
      {
        type: "button",
        class: "market-item",
        "aria-current": ticker === state.selected ? "true" : "false",
        onclick: () => selectTicker(ticker),
      },
      el("span", { class: "t" }, ticker),
      el("span", { class: "p num" }, money.format(lastPrice(ticker))),
      change,
    );
    return el("li", {}, button);
  });
  $("market-list").replaceChildren(...items);
}

let chart = null;

function ensureChart() {
  if (chart || typeof Chart === "undefined") return chart;

  const accent = cssVar("--accent");
  const muted = cssVar("--muted");
  const line = cssVar("--line");
  const ink = cssVar("--ink");

  chart = new Chart($("price-chart"), {
    type: "line",
    data: {
      labels: [],
      datasets: [{
        data: [],
        borderColor: accent,
        backgroundColor: accent + "22",
        pointBackgroundColor: accent,
        fill: true,
        tension: 0.25,
        borderWidth: 2,
        pointRadius: [],
        pointHoverRadius: 5,
      }],
    },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      animation: false,
      interaction: { mode: "index", intersect: false },
      plugins: {
        legend: { display: false },
        tooltip: {
          callbacks: {
            label: (context) => ` Close ${money.format(context.parsed.y)}`,
          },
        },
      },
      scales: {
        x: {
          grid: { display: false },
          ticks: { color: muted, maxRotation: 0, autoSkip: true, maxTicksLimit: 7 },
          border: { color: line },
        },
        y: {
          grid: { color: line },
          ticks: { color: muted, callback: (value) => money.format(value) },
          border: { display: false },
          title: { display: false, color: ink },
        },
      },
    },
  });
  return chart;
}

function renderChart() {
  const ticker = state.selected;
  const tick = ticker ? state.latest.get(ticker) : null;

  $("chart-title").textContent = ticker ?? "—";
  $("chart-last").textContent = tick ? money.format(Number(tick.close)) : "—";
  setChange($("chart-change"), ticker ? dayChange(ticker) : null);
  $("stat-open").textContent = tick ? money.format(Number(tick.open)) : "—";
  $("stat-high").textContent = tick ? money.format(Number(tick.high)) : "—";
  $("stat-low").textContent = tick ? money.format(Number(tick.low)) : "—";
  $("stat-volume").textContent = tick ? volumeFmt.format(Number(tick.volume)) : "—";

  const series = ticker ? sortedHistory(ticker) : [];
  const emptyNote = $("chart-empty");

  if (typeof Chart === "undefined") {
    emptyNote.hidden = false;
    emptyNote.textContent = "Chart library failed to load (no internet access?). Prices still update in the market list.";
    return;
  }

  emptyNote.hidden = series.length > 1;
  const instance = ensureChart();
  const dataset = instance.data.datasets[0];
  instance.data.labels = series.map(([date]) => date);
  dataset.data = series.map(([, close]) => close);
  // Highlight the simulated day the replay is currently on - during a
  // later lap that's somewhere mid-chart, not necessarily the last point.
  dataset.pointRadius = series.map(([date]) => (tick && date === tick.tradeDate ? 5 : 0));
  instance.update("none");
}

function selectTicker(ticker) {
  state.selected = ticker;
  $("trade-ticker").value = ticker;
  scheduleRender();
}

function holdingQuantity(ticker) {
  const holding = state.portfolio?.holdings?.find((h) => h.ticker === ticker);
  return holding ? Number(holding.quantity) : 0;
}

function renderTicket() {
  if (!state.session) return;

  const select = $("trade-ticker");
  const tickers = [...state.latest.keys()].sort();
  const current = [...select.options].map((o) => o.value).join(",");
  if (current !== tickers.join(",")) {
    select.replaceChildren(...tickers.map((t) => el("option", { value: t }, t)));
  }
  if (state.selected) select.value = state.selected;

  const ticker = select.value;
  const price = ticker ? lastPrice(ticker) : null;
  const qty = Number($("trade-qty").value);
  const validQty = Number.isFinite(qty) && qty > 0;
  const isBuy = state.side === "BUY";

  $("ticket-price").textContent = price != null ? money.format(price) : "—";
  $("ticket-est-label").textContent = isBuy ? "Est. cost" : "Est. proceeds";
  $("ticket-est").textContent = price != null && validQty ? money.format(price * qty) : "—";
  $("ticket-avail-label").textContent = isBuy ? "Cash available" : "Shares held";
  $("ticket-avail").textContent = isBuy
    ? (state.portfolio ? money.format(Number(state.portfolio.cash)) : "—")
    : shares.format(holdingQuantity(ticker));

  const submit = $("trade-submit");
  submit.textContent = ticker ? `${isBuy ? "Buy" : "Sell"} ${ticker}` : (isBuy ? "Buy" : "Sell");
  submit.classList.toggle("primary", isBuy);
  submit.classList.toggle("sell", !isBuy);
  submit.disabled = !ticker || price == null || !validQty || submit.dataset.busy === "true";
}

function renderPortfolio() {
  const portfolio = state.portfolio;
  if (!state.session || !portfolio) return;

  $("portfolio-owner").textContent = portfolio.name;

  let holdingsValue = 0;
  const rows = (portfolio.holdings ?? []).map((holding) => {
    // Prefer the live price from the feed so values move with each tick;
    // fall back to the price the backend knew when the portfolio loaded.
    const price = lastPrice(holding.ticker) ?? (holding.lastPrice != null ? Number(holding.lastPrice) : null);
    const quantity = Number(holding.quantity);
    const value = price != null ? price * quantity : null;
    holdingsValue += value ?? 0;

    return el(
      "tr",
      { class: "clickable", onclick: () => selectTicker(holding.ticker), title: `Show ${holding.ticker} chart` },
      el("td", { class: "mono" }, holding.ticker),
      el("td", { class: "r num" }, shares.format(quantity)),
      el("td", { class: "r num" }, price != null ? money.format(price) : "—"),
      el("td", { class: "r num" }, value != null ? money.format(value) : "—"),
    );
  });

  const cash = Number(portfolio.cash);
  const total = cash + holdingsValue;

  $("holdings-body").replaceChildren(...rows);
  $("holdings-empty").hidden = rows.length > 0;
  $("pf-cash").textContent = money.format(cash);
  $("pf-holdings").textContent = money.format(holdingsValue);
  $("pf-total").textContent = money.format(total);

  const pnl = total - STARTING_CASH;
  const pnlNode = $("pf-pnl");
  pnlNode.classList.remove("up", "down", "flat");
  pnlNode.classList.add(pnl > 0.005 ? "up" : pnl < -0.005 ? "down" : "flat");
  pnlNode.textContent = `${pnl >= 0 ? "+" : "−"}${money.format(Math.abs(pnl))} (${pct.format(pnl / STARTING_CASH)}) since start`;
}

function renderTrades() {
  const rows = state.trades.slice(0, 15).map((trade) => {
    const quantity = Number(trade.quantity);
    const price = Number(trade.price);
    const isBuy = trade.side === "BUY";
    return el(
      "tr",
      {},
      el("td", { class: "num" }, timeFmt.format(new Date(trade.executedAt))),
      el("td", {}, el("span", { class: `pill ${isBuy ? "buy" : "sell"}` }, isBuy ? "BUY" : "SELL")),
      el("td", { class: "mono" }, trade.ticker),
      el("td", { class: "r num" }, shares.format(quantity)),
      el("td", { class: "r num" }, money.format(price)),
      el("td", { class: "r num" }, money.format(price * quantity)),
    );
  });
  $("trades-body").replaceChildren(...rows);
  $("trades-empty").hidden = rows.length > 0;
}

function renderAccountBar() {
  const node = $("account");
  if (!state.session) {
    node.replaceChildren();
    return;
  }
  node.replaceChildren(
    el("span", { class: "who" }, "Signed in as ", el("strong", {}, state.session.username)),
    el("button", { type: "button", class: "btn small", onclick: () => logout() }, "Log out"),
  );
}

function renderAuthState() {
  const loggedIn = Boolean(state.session);
  $("auth-panel").hidden = loggedIn;
  $("trade-panel").hidden = !loggedIn;
  $("portfolio-panel").hidden = !loggedIn;
  $("trades-panel").hidden = !loggedIn;
  renderAccountBar();
}

// ---------------------------------------------------------------- account

async function refreshAccount() {
  if (!state.session) return;
  const [portfolio, trades] = await Promise.all([
    api("/me/portfolio", { auth: true }),
    api("/me/trades", { auth: true }),
  ]);
  state.portfolio = portfolio;
  state.trades = trades ?? [];
  renderPortfolio();
  renderTrades();
  renderTicket();
}

function onLoggedIn() {
  renderAuthState();
  scheduleRender();
  refreshAccount().catch((error) => showMessage($("trade-message"), error.message, "error"));
}

function logout(notice) {
  state.session = null;
  state.portfolio = null;
  state.trades = [];
  saveSession(null);
  renderAuthState();
  showMessage($("trade-message"), "", null);
  showMessage($("auth-message"), notice ?? "", "error");
  $("auth-password").value = "";
}

function setAuthMode(mode) {
  state.authMode = mode;
  const register = mode === "register";
  $("tab-login").setAttribute("aria-selected", String(!register));
  $("tab-register").setAttribute("aria-selected", String(register));
  $("auth-submit").textContent = register ? "Create account" : "Log in";
  $("auth-password").setAttribute("autocomplete", register ? "new-password" : "current-password");
  $("auth-hint").textContent = register
    ? "At least 6 characters. You'll start with $100,000 in cash."
    : "New accounts start with $100,000 in cash.";
  showMessage($("auth-message"), "", null);
}

async function submitAuth(event) {
  event.preventDefault();
  const username = $("auth-username").value.trim();
  const password = $("auth-password").value;
  const message = $("auth-message");

  if (!username || !password) {
    showMessage(message, "Enter a username and password.", "error");
    return;
  }
  if (state.authMode === "register" && password.length < 6) {
    showMessage(message, "Choose a password with at least 6 characters.", "error");
    return;
  }

  const submit = $("auth-submit");
  submit.disabled = true;
  try {
    const path = state.authMode === "register" ? "/auth/register" : "/auth/login";
    const result = await api(path, { method: "POST", body: { username, password } });
    state.session = { token: result.token, userId: result.userId, username: result.username };
    saveSession(state.session);
    $("auth-password").value = "";
    showMessage(message, "", null);
    onLoggedIn();
  } catch (error) {
    showMessage(message, error.message, "error");
  } finally {
    submit.disabled = false;
  }
}

// ---------------------------------------------------------------- trading

function setSide(side) {
  state.side = side;
  $("side-buy").setAttribute("aria-pressed", String(side === "BUY"));
  $("side-sell").setAttribute("aria-pressed", String(side === "SELL"));
  showMessage($("trade-message"), "", null);
  renderTicket();
}

async function submitTrade(event) {
  event.preventDefault();
  const ticker = $("trade-ticker").value;
  const quantity = Number($("trade-qty").value);
  const message = $("trade-message");

  if (!ticker || !Number.isFinite(quantity) || quantity <= 0) {
    showMessage(message, "Enter a number of shares greater than zero.", "error");
    return;
  }

  const submit = $("trade-submit");
  submit.dataset.busy = "true";
  renderTicket();
  try {
    const trade = await api("/trades", {
      method: "POST",
      auth: true,
      body: { ticker, side: state.side.toLowerCase(), quantity },
    });
    const verb = trade.side === "BUY" ? "Bought" : "Sold";
    showMessage(
      message,
      `${verb} ${shares.format(Number(trade.quantity))} ${trade.ticker} at ${money.format(Number(trade.price))}.`,
      "ok",
    );
    await refreshAccount();
  } catch (error) {
    showMessage(message, error.message, "error");
  } finally {
    submit.dataset.busy = "false";
    renderTicket();
  }
}

// ---------------------------------------------------------------- startup

function wireEvents() {
  $("tab-login").addEventListener("click", () => setAuthMode("login"));
  $("tab-register").addEventListener("click", () => setAuthMode("register"));
  $("auth-form").addEventListener("submit", submitAuth);

  $("side-buy").addEventListener("click", () => setSide("BUY"));
  $("side-sell").addEventListener("click", () => setSide("SELL"));
  $("trade-ticker").addEventListener("change", (event) => selectTicker(event.target.value));
  $("trade-qty").addEventListener("input", () => {
    showMessage($("trade-message"), "", null);
    renderTicket();
  });
  $("trade-form").addEventListener("submit", submitTrade);

  // Chart colors come from CSS variables - rebuild the chart when the OS
  // switches between light and dark so it picks up the other palette.
  window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => {
    chart?.destroy();
    chart = null;
    scheduleRender();
  });
}

function init() {
  wireEvents();

  const stored = loadSession();
  if (stored && !tokenExpired(stored.token)) {
    state.session = stored;
  } else if (stored) {
    saveSession(null);
  }

  renderAuthState();
  if (state.session) onLoggedIn();

  seedPrices();
  connectFeed();
}

init();
