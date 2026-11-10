# Compatibility: six open-source Dalamud plugins

*Version française plus bas.*

PluginUiTest 0.1.0 and 0.2.0 tried on six open-source Dalamud plugins built from their sources, on 2026-11-10. What broke because of
the tool is fixed in 0.2.0 ([CHANGELOG](../CHANGELOG.md)); what remains is said below as it was measured. Nothing of these plugins is
in this repository: only their names, links, licences and the commits that were built.

## Method

- Each repository cloned at the commit below (`--depth 1`, with its submodules) and built from source with
  `dotnet build -c Release`, .NET SDK 10.0.403, against Dalamud 15.0.3.9 (`%APPDATA%\XIVLauncher\addon\Hooks\dev`), Windows 11 x64.
- Each built DLL run in three modes, `--timeout=20`, with `--out=` and `--json=` reports:
  - `PluginUiTest <plugin>.dll` (game data read from the local install);
  - `PluginUiTest <plugin>.dll --no-game-data` (as in CI);
  - `PluginUiTest <plugin>.dll --run-anyway=*` (every part run, game memory included, still in the child process).
- "Before" is the v0.1.0 release, "after" the 0.2.0 build. Exit codes: 1 = something threw, blocked or crashed; 3 = the plugin could
  not be created ([README](../README.md#exit-codes)).

## Plugins

| Plugin | Licence | Commit | Built |
|---|---|---|---|
| [SimpleTweaksPlugin](https://github.com/Caraxi/SimpleTweaksPlugin) | AGPL-3.0 | `c039932` (2026-10-17) | yes |
| [ChatTwo](https://github.com/Infiziert90/ChatTwo) | EUPL-1.2 | `ca9aa99` (2026-10-28) | yes |
| [SubmarineTracker](https://github.com/Infiziert90/SubmarineTracker) | MIT | `04c54d7` (2026-10-03) | yes (native SQLite) |
| [Artisan](https://github.com/PunishXIV/Artisan) | BSD-3-Clause | `c8ee6e0` (2026-11-10) | yes |
| [Glamourer](https://github.com/Ottermandias/Glamourer) | Apache-2.0 | `e165910` (2026-10-08, branch `testing`) | **no**, see below |
| [InventoryTools](https://github.com/Critical-Impact/InventoryTools) | GPL-3.0 | `e3c3fc6` (2026-11-08) | yes |

Glamourer was not built, for reasons unrelated to Dalamud's API or to this tool: its `FlatSharp.Compiler` build step needs the .NET 9
x64 runtime (only .NET 10 was installed); with `DOTNET_ROLL_FORWARD=Major`, its `Penumbra.GameData` submodule then failed on
`ByteString` / `Penumbra.String` not found (submodules out of step at that commit). It was not measured.

## Results

The default mode and `--no-game-data` gave the same outcome for every plugin but InventoryTools (said in its row); they share one column.

| Plugin | Before (0.1.0), all modes | After (0.2.0), default and `--no-game-data` | After (0.2.0), `--run-anyway=*` |
|---|---|---|---|
| SimpleTweaks | created, 0 windows (its windows are built by work handed to `IFramework.RunOnFrameworkThread`, never run); a `UiBuilder.Draw` handler, `DebugManager.DrawUndockedPages`, threw `NullReferenceException`; exit 1 | the framework work `SimpleTweaksPlugin.Initialize` is shown and skipped (it reaches `IGameInteropProvider.InitializeFromAttributes`); the same `Draw` handler throws (its state is built by `Initialize`); exit 1 | `Initialize` throws `GameOnlyException` (`IGameInteropProvider.HookFromAddress`); 0 windows; exit 1 |
| ChatTwo | constructor skipped (`Plugin..ctor → GameFunctions..ctor → IGameInteropProvider.InitializeFromAttributes`), exit 3; `--run-anyway=*`: constructor threw `InvalidOperationException: Address for Framework.Instance is null` (FFXIVClientStructs, needs the game), exit 3 | unchanged: constructor skipped, exit 3 | unchanged: same exception, exit 3 |
| SubmarineTracker | constructor skipped (`Plugin..ctor → HookManager..ctor → FFXIVClientStructs: PacketDispatcher…HandleEventYieldPacket`), exit 3; `--run-anyway=*`: `NullReferenceException` in `Database.Dispose` after `DllNotFoundException: e_sqlite3` (**tool defect**: native dependency not found), exit 3 | unchanged: constructor skipped, exit 3 | its database is created (`e_sqlite3` found in Dalamud's `runtimes\win-x64\native`, no `DllNotFoundException` left in its log), the constructor goes on to `HookManager..ctor` and throws `GameOnlyException` (`HookFromAddress`), exit 3 |
| Artisan | constructor skipped (`Artisan..ctor → ECommonsMain.Init → ISigScanner.GetStaticAddressFromSig`), exit 3; `--run-anyway=*`: `TypeLoadException` on `Hook<T>` (**tool defect**: its proxy did not copy the `T : Delegate` constraint), exit 3 | unchanged: constructor skipped, exit 3; its 5 Dalamud libraries are rewritten with it | constructor throws `GameOnlyException` (`IGameInteropProvider.HookFromAddress`, `Artisan.cs:59`), exit 3 |
| InventoryTools | "no class implementing IDalamudPlugin" (**tool defect**: it implements only `IAsyncDalamudPlugin`), exit 3 | created (8 libraries rewritten with it); `LoadAsync` throws `InvalidOperationException` from its dependency container (`BootService → ConfigurationWizardService → …`); 2 pieces of framework work (`GameInterface`, `InventoryScanner`) skipped (`InitializeFromAttributes`); 0 windows; exit 1. With `--no-game-data`, `LoadAsync` throws earlier (`CriticalCommonLib.Services.MobTracker → Lumina.GameData`: it needs the game data) and no framework work is queued; exit 1 | the same `LoadAsync` exception; the 2 pieces of framework work run; 0 windows; exit 1 |
| Glamourer | not built | not built | not built |

Also measured with the same builds:

- [Tensho Assistant](https://github.com/Shigu330/Tensho-Assistant), where the tool was first written: by default its constructor is
  skipped (exit 3). Before, through `NativeMapReplacer.Dispose → IGameGui.GetAddonByName`, a release only kept for `Dispose` (false
  positive); after, through `Plugin..ctor → CompetitiveWatcher..ctor → Decide → InArenaInstance → GameMain.IsInPvPInstance`, a real call
  of its constructor. With `--run-anyway=constructor`: 2 drawn, 8 hidden, 3 skipped, exit 0 (unchanged); with `--run-anyway=*`: 5 drawn,
  8 hidden, exit 0.
- The sample (`samples/SamplePlugin`): its library window was reported blocked after 10 s by 0.1.0; drawn by 0.2.0.

## What this says

- Large plugins reach game memory from their constructor (hooks, signature scans, FFXIVClientStructs resolvers). By default the tool
  says so with the path it found and does not create them (exit 3): that is by design, not a failure of the tool.
- With `--run-anyway`, they now stop with a clear reason (`GameOnlyException`, an unresolved game address) instead of a defect of the
  tool. Four such defects were found by this test and are fixed in 0.2.0: `IAsyncDalamudPlugin`, framework work never run, native
  dependencies, and the `Hook<T>` proxy; libraries that use Dalamud are now rewritten with the plugin.
- None of the five plugins built drew a window in this measure. Hooks are not emulated ([README, Limits](../README.md#limits)). The
  tool helps most where a plugin's windows can be created without game memory in its constructor, as in the sample, or
  when its creator runs the constructor anyway on purpose (`--run-anyway=constructor`, as for Tensho Assistant above).

## Reproduce

```powershell
# from each plugin's folder, after cloning it at the commit above with its submodules
dotnet build <project>.csproj -c Release
# then, for each mode
PluginUiTest.exe <path>\bin\Release\<plugin>.dll --timeout=20 --out=report.md --json=report.json
PluginUiTest.exe <path>\bin\Release\<plugin>.dll --no-game-data --timeout=20
PluginUiTest.exe <path>\bin\Release\<plugin>.dll --run-anyway=* --timeout=20
```

The tool is a test tool, not affiliated with the Dalamud team, and does not replace Dalamud. These results describe these commits
only; a later version of a plugin can behave otherwise.

---

# Compatibilité : six plugins Dalamud open source

PluginUiTest 0.1.0 et 0.2.0 essayés sur six plugins Dalamud open source compilés depuis leurs sources, le 2026-11-10. Ce qui cassait à
cause de l'outil est corrigé en 0.2.0 ([CHANGELOG](../CHANGELOG.md)) ; ce qui reste est dit plus bas tel que mesuré. Rien de ces plugins
n'est dans ce dépôt : seulement leurs noms, liens, licences et les commits compilés.

## Méthode

- Chaque dépôt cloné au commit ci-dessous (`--depth 1`, avec ses sous-modules) et compilé depuis les sources par
  `dotnet build -c Release`, SDK .NET 10.0.403, contre Dalamud 15.0.3.9 (`%APPDATA%\XIVLauncher\addon\Hooks\dev`), Windows 11 x64.
- Chaque DLL produite lancée en trois modes, `--timeout=20`, avec les rapports `--out=` et `--json=` :
  - `PluginUiTest <plugin>.dll` (données du jeu lues dans l'installation locale) ;
  - `PluginUiTest <plugin>.dll --no-game-data` (comme en CI) ;
  - `PluginUiTest <plugin>.dll --run-anyway=*` (toutes les parties lancées, mémoire du jeu comprise, toujours dans le processus enfant).
- « Avant » est la release v0.1.0, « après » la build 0.2.0. Codes de sortie : 1 = une exception, un blocage ou un plantage ; 3 = le
  plugin n'a pas pu être créé ([README](../README.md#codes-de-sortie)).

## Plugins

| Plugin | Licence | Commit | Compilé |
|---|---|---|---|
| [SimpleTweaksPlugin](https://github.com/Caraxi/SimpleTweaksPlugin) | AGPL-3.0 | `c039932` (2026-10-17) | oui |
| [ChatTwo](https://github.com/Infiziert90/ChatTwo) | EUPL-1.2 | `ca9aa99` (2026-10-28) | oui |
| [SubmarineTracker](https://github.com/Infiziert90/SubmarineTracker) | MIT | `04c54d7` (2026-10-03) | oui (SQLite natif) |
| [Artisan](https://github.com/PunishXIV/Artisan) | BSD-3-Clause | `c8ee6e0` (2026-11-10) | oui |
| [Glamourer](https://github.com/Ottermandias/Glamourer) | Apache-2.0 | `e165910` (2026-10-08, branche `testing`) | **non**, voir plus bas |
| [InventoryTools](https://github.com/Critical-Impact/InventoryTools) | GPL-3.0 | `e3c3fc6` (2026-11-08) | oui |

Glamourer n'a pas été compilé, pour des raisons sans lien avec l'API de Dalamud ni avec cet outil : son étape `FlatSharp.Compiler` veut
le runtime .NET 9 x64 (seul .NET 10 était installé) ; avec `DOTNET_ROLL_FORWARD=Major`, son sous-module `Penumbra.GameData` échouait
ensuite sur `ByteString` / `Penumbra.String` introuvables (sous-modules désaccordés à ce commit). Il n'a pas été mesuré.

## Résultats

Le mode par défaut et `--no-game-data` ont donné le même résultat pour chaque plugin sauf InventoryTools (dit dans sa ligne) ; ils partagent une colonne.

| Plugin | Avant (0.1.0), tous les modes | Après (0.2.0), par défaut et `--no-game-data` | Après (0.2.0), `--run-anyway=*` |
|---|---|---|---|
| SimpleTweaks | créé, 0 fenêtre (ses fenêtres sont construites par un travail donné à `IFramework.RunOnFrameworkThread`, jamais lancé) ; un abonné de `UiBuilder.Draw`, `DebugManager.DrawUndockedPages`, lève `NullReferenceException` ; sortie 1 | le travail du framework `SimpleTweaksPlugin.Initialize` est montré et sauté (il atteint `IGameInteropProvider.InitializeFromAttributes`) ; le même abonné de `Draw` lève une exception (son état est construit par `Initialize`) ; sortie 1 | `Initialize` lève `GameOnlyException` (`IGameInteropProvider.HookFromAddress`) ; 0 fenêtre ; sortie 1 |
| ChatTwo | constructeur sauté (`Plugin..ctor → GameFunctions..ctor → IGameInteropProvider.InitializeFromAttributes`), sortie 3 ; `--run-anyway=*` : le constructeur lève `InvalidOperationException: Address for Framework.Instance is null` (FFXIVClientStructs, a besoin du jeu), sortie 3 | inchangé : constructeur sauté, sortie 3 | inchangé : même exception, sortie 3 |
| SubmarineTracker | constructeur sauté (`Plugin..ctor → HookManager..ctor → FFXIVClientStructs: PacketDispatcher…HandleEventYieldPacket`), sortie 3 ; `--run-anyway=*` : `NullReferenceException` dans `Database.Dispose` après `DllNotFoundException: e_sqlite3` (**défaut de l'outil** : dépendance native introuvable), sortie 3 | inchangé : constructeur sauté, sortie 3 | sa base de données est créée (`e_sqlite3` trouvé dans `runtimes\win-x64\native` de Dalamud, plus de `DllNotFoundException` dans son journal), le constructeur va jusqu'à `HookManager..ctor` et lève `GameOnlyException` (`HookFromAddress`), sortie 3 |
| Artisan | constructeur sauté (`Artisan..ctor → ECommonsMain.Init → ISigScanner.GetStaticAddressFromSig`), sortie 3 ; `--run-anyway=*` : `TypeLoadException` sur `Hook<T>` (**défaut de l'outil** : son proxy ne recopiait pas la contrainte `T : Delegate`), sortie 3 | inchangé : constructeur sauté, sortie 3 ; ses 5 bibliothèques Dalamud sont réécrites avec lui | le constructeur lève `GameOnlyException` (`IGameInteropProvider.HookFromAddress`, `Artisan.cs:59`), sortie 3 |
| InventoryTools | « no class implementing IDalamudPlugin » (**défaut de l'outil** : il n'implémente que `IAsyncDalamudPlugin`), sortie 3 | créé (8 bibliothèques réécrites avec lui) ; `LoadAsync` lève `InvalidOperationException` depuis son conteneur de dépendances (`BootService → ConfigurationWizardService → …`) ; 2 travaux du framework (`GameInterface`, `InventoryScanner`) sautés (`InitializeFromAttributes`) ; 0 fenêtre ; sortie 1. Avec `--no-game-data`, `LoadAsync` lève plus tôt (`CriticalCommonLib.Services.MobTracker → Lumina.GameData` : il a besoin des données du jeu) et aucun travail du framework n'est donné ; sortie 1 | la même exception de `LoadAsync` ; les 2 travaux du framework tournent ; 0 fenêtre ; sortie 1 |
| Glamourer | non compilé | non compilé | non compilé |

Mesurés aussi avec les mêmes builds :

- [Tensho Assistant](https://github.com/Shigu330/Tensho-Assistant), où l'outil a d'abord été écrit : par défaut son constructeur est
  sauté (sortie 3). Avant, par `NativeMapReplacer.Dispose → IGameGui.GetAddonByName`, une libération seulement gardée pour `Dispose`
  (faux positif) ; après, par `Plugin..ctor → CompetitiveWatcher..ctor → Decide → InArenaInstance → GameMain.IsInPvPInstance`, un vrai
  appel de son constructeur. Avec `--run-anyway=constructor` : 2 dessinées, 8 cachées, 3 sautées, sortie 0 (inchangé) ; avec
  `--run-anyway=*` : 5 dessinées, 8 cachées, sortie 0.
- L'exemple (`samples/SamplePlugin`) : sa fenêtre de bibliothèque était dite bloquée après 10 s par 0.1.0 ; dessinée par 0.2.0.

## Ce que cela dit

- Les gros plugins atteignent la mémoire du jeu dès leur constructeur (crochets, recherches de signatures, résolveurs de
  FFXIVClientStructs). Par défaut l'outil le dit avec le chemin trouvé et ne les crée pas (sortie 3) : c'est voulu, pas un échec de
  l'outil.
- Avec `--run-anyway`, ils s'arrêtent maintenant sur une raison claire (`GameOnlyException`, une adresse du jeu non résolue) au lieu d'un
  défaut de l'outil. Quatre défauts de ce genre ont été trouvés par cet essai et sont corrigés en 0.2.0 : `IAsyncDalamudPlugin`, travail
  du framework jamais lancé, dépendances natives, et le proxy de `Hook<T>` ; les bibliothèques qui utilisent Dalamud sont maintenant
  réécrites avec le plugin.
- Aucun des cinq plugins compilés n'a dessiné de fenêtre dans cette mesure. Les crochets ne sont pas imités
  ([README, Limites](../README.md#limites)). L'outil sert le plus quand les fenêtres d'un plugin peuvent être créées sans mémoire du jeu
  dans son constructeur, comme dans l'exemple, ou quand son créateur lance exprès le constructeur quand même
  (`--run-anyway=constructor`, comme pour Tensho Assistant plus haut).

## Reproduire

```powershell
# depuis le dossier de chaque plugin, après l'avoir cloné au commit ci-dessus avec ses sous-modules
dotnet build <projet>.csproj -c Release
# puis, pour chaque mode
PluginUiTest.exe <chemin>\bin\Release\<plugin>.dll --timeout=20 --out=report.md --json=report.json
PluginUiTest.exe <chemin>\bin\Release\<plugin>.dll --no-game-data --timeout=20
PluginUiTest.exe <chemin>\bin\Release\<plugin>.dll --run-anyway=* --timeout=20
```

L'outil est un outil de test, non affilié à l'équipe de Dalamud, et ne remplace pas Dalamud. Ces résultats décrivent ces commits
seulement ; une version plus récente d'un plugin peut se comporter autrement.
