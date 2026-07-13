# FleetFinder

A Windows desktop tool for **Elite Dangerous: Odyssey** that finds fleet carriers trading the engineering components you still need, tracks what you already hold, and lets you plan suit and weapon builds without doing the searching yourself.

## What it does

The app has three tabs:

- **Find Carriers:** Pick the individual items you're after and see how much you currently hold. Tick the ones you're short on and hit search. Results are grouped one row per carrier, showing what they sell, the price, the distance from your current system, and how recently the listing was updated. Click a system name to copy it to the clipboard for easy pasting into the in-game galaxy map.

  <img width="1764" height="1106" alt="Screenshot 2026-07-11 193305" src="https://github.com/user-attachments/assets/54f708cf-3ca8-48cc-9ea6-492be3e8d967" />

- **Modifications:** Every suit/weapon modification and suit upgrade with the components it needs. Tick the ones you want (or use the stepper to pick multiples of that mod) and hit **Apply selected**, this sets a target on the Find Carriers tab for everything those mods require and auto-selects whatever you're still short on, so you can go straight to searching. Once you have purchased the required amount, the app will automatically deselect those items so you can just hit search again when you're ready to find the next carrier!

  <img width="1712" height="1439" alt="Screenshot 2026-07-11 202206" src="https://github.com/user-attachments/assets/01f95ce0-eb1b-441f-99a3-ec1da008c905" />

- **Import:** Upload a wishlist exported from **EDOMH** (<a href="URL">[Elite Dangerous Odyssey Materials Helper](https://github.com/jixxed/ed-odyssey-materials-helper)</a>). FleetFinder matches each line against the component catalogue and auto-selects what you still need, so you can plan a build in EDOMH, export it, and one click find where to buy it.

<img width="1711" height="1439" alt="Screenshot 2026-07-12 133722" src="https://github.com/user-attachments/assets/7e1d67d8-b4a6-4fae-b5ff-aaed86ebea81" />

And most importantly, if you have a lot of materials to search for and you don't get them all finished in one sitting, the app will prompt you the next time you open the app so you can pick up exactly where you left off!

<img width="449" height="211" alt="Screenshot 2026-07-12 124135" src="https://github.com/user-attachments/assets/1e9fd748-e944-44f8-9011-b0450a19cf38" />

## Where the market data comes from

Carrier listings come from the wider Elite Dangerous community's shared data network (EDDN), the same public feed that many other Elite Dangerous tools rely on. Coverage grows over time as more players play the game, so rarely traded components may show fewer results at first. Please use a journal reporting tool such as <a href="URL">[EDDiscovery](https://github.com/EDDiscovery/EDDiscovery)</a>, <a href="URL">[EDMarketConnector](https://github.com/EDCD/EDMarketConnector)</a> or similar to help gather and share the data we need.
Components only get updated to EDDN **when a cmdr opens the bartender commodity list**. In order to keep this running as smoothly and as accurately as possible, I ask that once you have been to a carrier and bought what you need, **please close the bartender menu and then open it again**. This will update EDDN to reflect the changes in inventory stock after you have bought what you need and ensure stock levels are as current as possible.

## Requirements

- **Windows 10 or 11.** It doesn't run on macOS or Linux.
- **Elite Dangerous: Odyssey**, launched at least once so the game has written your inventory file. This is detected automatically, there's no folder to set up.
- Internet access, for carrier search and distance lookups. Both are optional, the app still runs and shows your inventory without them.

## Installing

Download the latest release, then run `FleetFinder.exe`. Keep the `Data` folder that comes with it in the same place as the exe. No installer and nothing else to set up, it's a single self-contained program.

## Known limitations

- Carrier docking access may show as "Unknown" if that hasn't been reported yet. This fills in over time as more players visit carriers, and like any shared community data it can occasionally be out of date by the time you arrive. Since this is a new release there will likely be more "unknown" than "yes" currently, but this will improve as people continue to dock at carriers and my server gets that info from EDDN.

- The component catalogue is curated for suit and weapon engineering, upgrades, and engineer unlocks, not every Odyssey component that exists. If an EDOMH import shows an item it doesn't recognize, that's flagged in the Import tab as "false" so you can see exactly what wasn't matched. If this happens, please let me know so it can be added.
