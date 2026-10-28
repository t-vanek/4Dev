# TString export

Exports the Czech text table from a locally installed 4Story client without modifying either
installation. Reads MFC CArchive CString lengths, UTF-16 markers and the nation's Windows code
page. Keeps duplicate records in their original order; the game uses the final occurrence of an ID.

```powershell
dotnet run --project Tools/TStringExport -- --self-test
dotnet run --project Tools/TStringExport -- 'C:\Program Files (x86)\4Story' Game/Tcd/TString.tcd .runtime/localization/gameforge-cs
```

Choose a new output folder on each run. It contains verified original copies, UTF-8 JSON exports,
checksums and a comparison against our current table. Keep these generated donor assets local in
the ignored `.runtime/` folder.

The ID comparison is diagnostic. Matching IDs or printf arguments do not prove matching meanings
between different game versions. Item names, quests and UI labels can live outside TString.tcd.

The reviewed `czech-map.json` is tied to SHA-256 fingerprints of our original English table and
the installed Czech donor. It maps the shifted blocks and explicitly handles later rearrangements
and custom gameplay messages. Technical paths, URLs, format-only fields and GM commands remain
unchanged. The merge validates every printf argument and retains the original record IDs/order.

```powershell
# Use the ORIGINAL English table from the backup after Czech has been deployed.
dotnet run --project Tools/TStringExport -- --merge Game/Tcd/TString.tcd 'C:\Program Files (x86)\4Story\Tcd\TString.tcd' Tools/TStringExport/czech-map.json .runtime/localization/czech-ready
node Tools/TStringExport/localize-ui.ts Game/TClientCmd.tif .runtime/localization/czech-ready/ui-dictionary.json .runtime/localization/czech-ready/TClientCmd.tif .runtime/localization/czech-ready/ui-report.json
```

`localize-ui.ts` uses the existing UI editor codec. It requires Node with native TypeScript support,
translates unambiguous matching captions/tooltips into CP1250, and verifies every non-text field.
It leaves image resources, component IDs, dimensions, commands, shared lists and fonts intact.

Both tools stage new files and reports, without overwriting an existing output or deploying them.
Before copying the staged files into `Game`, close the client, back up `TString.tcd`,
`TClientCmd.tif`, `TStringCF.tcd`, `config.ini` and `TClient.exe`, and verify their hashes. Retain
the reports beside that backup. The Gameforge installation is only read.

For the full menu pass, first convert the donor UI with `Tools/TifDedupe/tif-dedupe.mjs`, then use
`ui-donor.ts original-English.tif donor-v2.tif report.json` to compare the same frame/control IDs.
`prepare-ui.ts report.json TString-dictionary.json NEW-directory` resolves unambiguous translations,
applies the reviewed `ui-overrides.json`, and reports remaining strings. Pass its dictionary to
`localize-ui.ts`. Generate from the original English UI, not a previously localized file.

`prepare-ui.ts` also generates `CzechUiText.inc` for the explicit `CTChart::Localize` call sites.
Copy it into `Lib/Own/TChart/TChart/` and rebuild the client. The checked-in generated include lets
normal builds run without a donor installation or Node. Its byte escapes preserve CP1250 independently
of the compiler's source encoding. The helper only applies to fixed UI text and retains English
when another nation is selected; player names, chat, commands and network data do not pass through it.

The menu deployment covers the compiled UI and fixed client captions, including custom game options,
keyboard descriptions, companion controls and security-code menus. It also changes text fields in
`TDynamicH`, `THelp`, `TMenuItem`, `TAuctionTree`, `TCountryInfo`, `TClassInfo` and `TRaceInfo`.
Their IDs, counts and every non-text byte were verified unchanged; only CString lengths/text changed.
Restart an already running game to reload these charts.

Runtime captions and messages have a separate reviewed dictionary in `runtime-overrides.json`.
`prepare-ui.ts` checks `CTChart::Localize` calls in every client `.cpp`, validates format arguments and
generates CP1250 literals. Runtime overrides do not replace context-specific captions in the binary UI.
`audit-ui.ts Game/TClientCmd.tif NEW-report.json` inventories all frames, controls and command references,
checks a byte-identical round trip, and finds literal-caption dispatch and mismatched hit-test/visibility
controls. An unmatched resource command is a review item: constructors and manual handlers can replace it.

From a Visual Studio developer shell, run `python Tools/GameforgeReference/test_quest_execution.py`,
`test_quest_buttons.py` and `test_ui_actions.py` (in the same directory) for native regression checks.
They compile the actual client handler bodies with recording UI/session fixtures. They do not launch
the graphical game or modify gameplay data; they do not establish a graphical playthrough.

## Item, NPC and quest content

The content pass keeps our original charts and replaces text spans only. It supports both quest-file
versions, both skill layouts, legacy NPC maps and the NPC name index. Every output is parsed again;
removing all text spans from the old/new files must leave identical bytes. This checks IDs, statistics,
positions, counts, quest terms/rewards and all other binary data. Placeholder/quantity contracts and
Windows-1250 encoding are checked separately. Never copy a complete retail gameplay table over ours.

Prepare from the **original English content backup**, including the port markers in `Tcd/*.applied`.
The tool rejects already localized items. The first pass uses matching Gameforge translations and
unambiguous translation memory. Reviewed custom names, NPC roles, titles and patterns are in
`content-overrides.json`, `reviewed-names.tsv`, `dialog-overrides.json` and `legacy-npc.tsv`. Legacy Korean names use canonical
NPC IDs where possible, translated roles/objects and Latin romanization for unmapped personal names.

```powershell
dotnet run --project Tools/TStringExport -c Release -- --prepare-content ENGLISH-BACKUP/Game 'C:/Program Files (x86)/4Story' NEW-PLAN
python Tools/TStringExport/bootstrap-mt.py .runtime/localization
python Tools/TStringExport/translate-content.py NEW-PLAN .runtime/localization TRANSLATIONS.jsonl
python Tools/TStringExport/translate-content.py NEW-PLAN .runtime/localization TRANSLATIONS.jsonl --sentences
dotnet run --project Tools/TStringExport -c Release -- --apply-content NEW-PLAN/plan.json TRANSLATIONS.jsonl NEW-OUTPUT
```

The optional Python translation step runs **locally**. Use a dedicated virtual environment,
`requirements-local-mt.txt`, and CPU PyTorch from `https://download.pytorch.org/whl/cpu` (validated with
PyTorch 2.14.1+cpu). No game text is sent to an online translation service. The sentence pass retains
every sentence instead of allowing a sentence model to shorten a whole dialogue. JSONL caches are
resumable; keep them with `plan.json`, the model revision and output reports for later language edits.
Source hashes must still match at apply/deployment time. Stage first, verify a backup, then deploy.

Missing older dialogue uses [Helsinki-NLP OPUS-MT English–Czech/Slovak](https://huggingface.co/Helsinki-NLP/opus-mt-tc-big-en-ces_slk),
target `>>ces<<`, converted to int8 with CTranslate2. This is an automatic draft where no original Czech
translation exists; binary/placeholder verification does not replace editorial language review.
Attribution: Jörg Tiedemann and Santhosh Thottingal, [OPUS-MT — Building open translation services for
the World](https://aclanthology.org/2020.eamt-1.61/), EAMT 2020; Helsinki-NLP. Model license: CC-BY-4.0.

`--self-test` also exercises long/Unicode CString fields, quest terms/rewards, malformed archives,
legacy NPC encodings and rejected placeholder/quantity changes. Generated model files, caches,
backups and diagnostics stay in the ignored `.runtime/` directory.
