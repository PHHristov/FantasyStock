Fantasy Stock
One-line pitch: Create a multiplayer web app where friends can join ‘leagues’ and trade stocks or crypto using fake currency, tracking their portfolios in real-time using various metrics.
Owner: [name]	Date: [yyyy-mm-dd]	Stage: idea / prototype / pilot

1. PROBLEM
•	Who has this pain: Beginner investors, students, and competitive friend groups who want to learn how to trade but are afraid of losing real money.
•	How big/frequent is it: High frequency. Crypto and stock markets move fast, but the barrier to entry (fear of capital loss, intimidating interfaces, complex setups) keeps many on the sidelines.
•	What they do today (workaround):
•	Excel/Google Sheets: Manually logging trades and looking up prices (slow, boring, and zero real-time excitement).
•	Isolated Demo Accounts: Using retail brokerages that offer "paper trading" but lack social/competitive features, making the learning process lonely and dry.
	2. SOLUTION
•	Core idea in plain language: A web-based "fantasy league" where you and your friends start with a virtual cash balance (e.g., $100,000) and compete in private leagues to see who can build the most valuable crypto portfolio using live market prices.
•	Why this approach solves it:
•	Zero Risk: Fully sandbox-based; users learn by doing without financial consequences.
•	Gamification: Turn learning into a game. The leaderboard and live ticker feed drive daily user engagement and friendly rivalry.


3. TARGET USERS & VALUE
•	Primary user: Tech-savvy friend groups, retail investing beginners, and online finance communities.
•	Value delivered:
•	Risk Elimination: Safe sandbox environment to backtest strategies.
•	Time Saved: Automated real-time portfolio valuation and leaderboard calculations (no more manual spreadsheet tracking).
•	Social Connection: Active engagement through competitive interaction.
	4. MVP SCOPE (in / out)
To launch a functional, polished product within a reasonable timeframe, we must ruthlessly prioritize features:
In Scope(v1):
Real-Time Dashboard: Live price tickers of top stocks/crypto
Basic Trade Execution: Standard “Market” Buy/Sell orders that instantly execute against the live price 
Private Leagues: User can create a league with a custom start data and starting cash and invite others via a unique code.
Real-Time Leaderboard: A live-updating league table

5. SUCCESS METRIC (how we'll know it worked)
•	Technical Success: Under 2 seconds latency from public price changes to UI updates across all active users in a league.
•	Engagement Goal: Host a test league with 5 to 10 friends running continuously for 1 week with at least 3 trades logged per person by October 15, 2026.
	6. KEY ASSUMPTIONS & DEPENDENCIES
•	Data Availability: We assume free public cryptocurrency price APIs (like CoinGecko or CoinCap) remain stable and do not strictly rate-limit our server's IP address.

7. TEAM & RESOURCES NEEDED
•  Roles/skills, est. effort (person-weeks)
•  Budget / tools / infra, if any	8. ROUGH TIMELINE / MILESTONES
M1: Core Engine & Data
M2: UI &Authentitaction Trading 
M3: Gamification & Launch 

9. PRE-MORTEM — "It's 6 months later and this project failed. Why?"
Top risks (rank by likelihood × impact):
Risk	Why it could happen	Mitigation / early warning sign
No adoption	e.g. users don't feel enough pain	e.g. validate with 5 users before build
Technical blocker	...	...
Lost priority / funding	...	...

10. THE ASK — what decision or resource do you need right now?
e.g. "Approve 2 devs for 6 weeks to build a prototype" / "Feedback on scope" / "Budget of $X"

# POC
1. Download and import data into sql db
   - Postgres
   - 10 stock for 1 day.
   - Dockerized db
   - download data with yfinance (python).
   - Stefan
2. Kafka
   - Create .Net producer that gets data from Postgres and sends it to the cluster.
4. Backend DB - same as above ^
3. Backend in .NET
   - consumer that gets the data from the kafka cluster.
   - two users with their own data - no auth
   - buy & sell
   - historical data
