# FleetFinder

A Windows desktop tool for **Elite Dangerous: Odyssey** that finds fleet carriers trading the engineering components you still need, tracks what you already hold, and lets you plan suit and weapon builds without doing the searching yourself.

## What it does

The app has three tabs:

- **Find Carriers:** Pick the individual items yourse and see how many you currently hold. Tick the ones you're short on and hit search. Results are grouped one row per carrier, showing what they sell, the price, the distance from your current system, and how recently the listing was updated. Click a system name to copy it to the clipboard for pasting in the in-game galaxy map.

  <img width="1764" height="1106" alt="Screenshot 2026-07-11 193305" src="https://github.com/user-attachments/assets/54f708cf-3ca8-48cc-9ea6-492be3e8d967" />

- **Modifications:** Every suit and weapon modification and suit upgrade with the components it needs. Tick the ones you want (or use the stepper to pick multiples of that mod) and hit **Apply selected**, this sets a target on the Find Carriers tab for everything those mods require and auto-selects whatever you're still short on, so you can go straight to searching.

  <img width="1712" height="1439" alt="Screenshot 2026-07-11 202206" src="https://github.com/user-attachments/assets/01f95ce0-eb1b-441f-99a3-ec1da008c905" />

- **Import:** Upload a wishlist exported from **EDOMH** (Elite Dangerous Odyssey Materials Helper). FleetFinder matches each line against the component catalogue and auto-selects what you still need, so you can plan a build in EDOMH, export it, and one click find where to buy it.

  <img width="1711" height="1439" alt="Screenshot 2026-07-11 202347" src="https://github.com/user-attachments/assets/ccf7cc63-d875-4a0f-a6bf-cc7bf7ddc884" />

Your inventory (the "Have" column) is read straight from the game and refreshes automatically while you play, so it stays up to date without you doing anything.

## Where the market data comes from

Carrier listings come from the wider Elite Dangerous community's shared data network (EDDN), the same public feed that many other Elite Dangerous tools rely on. There's nothing to set up. Coverage grows over time as more players play the game, so newly added or rarely traded components may show fewer results at first.

## Requirements

- **Windows 10 or 11.** It doesn't run on macOS or Linux.
- **Elite Dangerous: Odyssey**, launched at least once so the game has written your inventory file. This is detected automatically, there's no folder to set up.
- Internet access, for carrier search and distance lookups. Both are optional, the app still runs and shows your inventory without them.

## Installing

Download the latest release, then run `FleetFinder.exe`. Keep the `Data` folder that comes with it in the same place as the exe. No installer and nothing else to set up, it's a single self-contained program.

## Known limitations

- Whether you can dock at a carrier may show as "Unknown" if that hasn't been reported yet. This fills in over time as more players visit carriers, and like any shared community data it can occasionally be out of date by the time you arrive.
- The component catalogue is curated for suit and weapon engineering, not every Odyssey material that exists. If an EDOMH import shows an item it doesn't recognize, that's flagged in the Import tab so you can see exactly what wasn't matched. If this happens, please let me know so it can be added.
