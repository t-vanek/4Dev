# Project priority: restore the Gameforge baseline first

The user wants a complete, playable reconstruction of the official Gameforge 4Story game before any custom gameplay changes.

- Follow `Gameforge/target.json` and the pinned client in `Gameforge/reference.json`. The documented content baseline is 5.0.1; the selected executable's exact release remains unknown. Do not silently switch patches or combine conflicting rules.
- Prioritize the normal player journey: account and character lifecycle, movement and portals, combat and skills, regeneration and revival, progression, quests, drops, equipment, economy, groups, persistence and the official game modes.
- Do not add custom balance, boosted starts, rates, rewards, convenience gameplay, cosmetics or new content while the baseline remains incomplete, unless the user explicitly changes this priority. Preserve existing unrelated work.
- Prefer publisher evidence and the pinned original client for content and balance. The inherited C++ server and SQL data are implementation references, not proof of Gameforge parity. Do not invent missing spawn positions, probabilities, formulas or quest rules.
- Fix reproducible functional defects and necessary integrity checks without presenting them as certified publisher balance changes. Add regression coverage for material gameplay fixes and preserve valid client packet sequences.
- Before changing an existing .NET implementation, inspect the relevant `git log`, commit diffs and `git blame`, together with its regression tests. Earlier C# changes can be deliberate fixes to the inherited C++; a difference from C++ is not by itself a defect. Preserve those fixes unless a reproduced defect and stronger evidence justify a specific change, and record that reasoning. Check every existing entry point when moving shared gameplay rules.
- Passing unit tests is one verification layer. Baseline completion also requires database round trips, persistence across logout/restart, and real-client playthroughs of the required player journeys. Keep missing evidence and incomplete features visible in `Gameforge/baseline-readiness.json`.
- Distinguish implemented, tested, deployed and verified against the reference. Never describe the whole server as complete, bug-free or equivalent to Gameforge solely because a build or test suite passes.
