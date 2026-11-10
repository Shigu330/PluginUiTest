# Changelog

*Version française plus bas.*

## 0.2.0

Tried on six open-source Dalamud plugins built from their sources (report: [docs/compatibility.md](docs/compatibility.md)); what broke
or was wrong is fixed here.

- **Libraries beside the plugin.** The DLLs of the plugin's folder that use Dalamud (ECommons, OtterGui, KamiToolKit, your own) are
  rewritten with the plugin, beside its copy: their `UiBuilder.IconFont`, `DefaultFont`, `MonoFont` and `WindowSystem.Draw` no longer wait
  for Dalamud's services. Before, a window drawn by such a library was reported **blocked** after `--timeout`. The game-memory scan reads
  those rewritten copies.
- **`IAsyncDalamudPlugin`.** A plugin whose class implements only `IAsyncDalamudPlugin` (Dalamud API 15) was refused with "no class
  implementing IDalamudPlugin" (exit 3); it is now created and its `LoadAsync` awaited, as the part `load` (measured on InventoryTools).
- **Framework work.** The work a plugin hands to `IFramework.RunOnFrameworkThread`, `RunOnTick` or `Run` was never run, so a plugin
  that builds its windows there showed none (SimpleTweaks). It is now kept and run, each piece as its own part `framework`, scanned for
  game memory first, after the constructor, while `LoadAsync` waits, and after the main and settings UI are opened; a new drawing
  process runs again the load and framework work that had run.
- **Native and other dependencies.** What the plugin's `.deps.json` names is found as Dalamud's plugin load context finds it, native
  libraries in `runtimes\win-x64\native` beside the plugin, then Dalamud's own folder (SQLite's `e_sqlite3`: `DllNotFoundException`
  before, measured on SubmarineTracker).
- **Game hooks.** `IGameInteropProvider` has its own stand-in: `InitializeFromAttributes` does nothing (the `[Signature]` members stay
  empty, as when a signature is not found) and every `Hook*` throws `GameOnlyException` ("game hooks need the running game"). Before,
  under `--run-anyway`, a plugin whose library uses `Hook<T>` failed with a `TypeLoadException` of the tool's own proxy (Artisan).
- **Game-memory scan, fewer false positives.** A call to a virtual or interface method now follows only the overrides in the types that
  derive from the called one or implement its interface, never for a final method or a sealed type (before, a call to one class's
  `Dispose` followed every `Dispose` of the plugin). A delegate only kept in a field of the plugin (stored, or added to a collection held in
  a field, directly or through the plugin's own methods, such as a release kept for `Dispose`) is followed only if a method on the path
  reads that field otherwise than to add to it; a delegate handed to Dalamud through the plugin's own helper is not followed. Measured on
  Tensho Assistant: its constructor was flagged through `NativeMapReplacer.Dispose` (only kept for unloading); it is now flagged through a
  real call of its constructor (`CompetitiveWatcher.Decide → GameMain.IsInPvPInstance`).
- **`--run-anyway` names.** A window can be named by its title, its `###id` (with or without `###`) or its full name; the option can be
  repeated; `\,` keeps a comma in a name. The report's hint gives a name that designates only that window (its `###id` when two windows
  share a title).
- Sample: a fourth window, drawn by `samples/SampleLibrary` beside the plugin; the CI checks it is drawn. Tests: the scan on fixtures
  (kept releases, a constructor that disposes, a kept delegate called back, abstract and interface calls) and the `--run-anyway` names.
- Report: the parts `load` and `framework` (kinds added to the JSON schema, `schemaVersion` stays 1) say `ran`. The console aligns `BLOCKED` and
  `CRASHED` with the other outcomes; the rewritten calls are counted on every run, not only the first.

## 0.1.0

First release: draws every window of a Dalamud plugin without the game, in a child process, and reports what was drawn, hidden, threw,
blocked, crashed or skipped because its code can reach game memory; JSON report (schema version 1); CI example.

---

# Journal des versions

## 0.2.0

Essayé sur six plugins Dalamud open source compilés depuis leurs sources (rapport : [docs/compatibility.md](docs/compatibility.md)) ; ce
qui cassait ou était faux est corrigé ici.

- **Bibliothèques à côté du plugin.** Les DLL du dossier du plugin qui utilisent Dalamud (ECommons, OtterGui, KamiToolKit, les vôtres)
  sont réécrites avec le plugin, à côté de sa copie : leurs `UiBuilder.IconFont`, `DefaultFont`, `MonoFont` et `WindowSystem.Draw`
  n'attendent plus les services de Dalamud. Avant, une fenêtre dessinée par une telle bibliothèque était dite **bloquée** après
  `--timeout`. L'analyse de la mémoire du jeu lit ces copies réécrites.
- **`IAsyncDalamudPlugin`.** Un plugin dont la classe n'implémente que `IAsyncDalamudPlugin` (API 15 de Dalamud) était refusé par
  « no class implementing IDalamudPlugin » (sortie 3) ; il est maintenant créé et son `LoadAsync` attendu, comme partie `load` (mesuré
  sur InventoryTools).
- **Travail du framework.** Le travail qu'un plugin donne à `IFramework.RunOnFrameworkThread`, `RunOnTick` ou `Run` ne tournait jamais :
  un plugin qui construit ses fenêtres là n'en montrait aucune (SimpleTweaks). Il est maintenant gardé et lancé, chaque morceau comme sa
  propre partie `framework`, analysée d'abord pour la mémoire du jeu, après le constructeur, pendant que `LoadAsync` attend, et après
  l'ouverture des interfaces principale et de réglages ; un nouveau processus de dessin relance le chargement et le travail du framework
  qui avaient tourné.
- **Dépendances natives et autres.** Ce que nomme le `.deps.json` du plugin est trouvé comme le contexte de chargement de Dalamud le
  trouve, les bibliothèques natives dans `runtimes\win-x64\native` à côté du plugin, puis le dossier de Dalamud (`e_sqlite3` de SQLite :
  `DllNotFoundException` avant, mesuré sur SubmarineTracker).
- **Crochets du jeu.** `IGameInteropProvider` a son propre remplaçant : `InitializeFromAttributes` ne fait rien (les membres
  `[Signature]` restent vides, comme quand une signature n'est pas trouvée) et chaque `Hook*` lève `GameOnlyException` (« game hooks
  need the running game »). Avant, sous `--run-anyway`, un plugin dont une bibliothèque utilise `Hook<T>` échouait sur une
  `TypeLoadException` du proxy de l'outil lui-même (Artisan).
- **Analyse de la mémoire du jeu, moins de faux positifs.** Un appel à une méthode virtuelle ou d'interface ne suit plus que les
  redéfinitions des types qui dérivent du type appelé ou implémentent son interface, jamais pour une méthode finale ni un type scellé
  (avant, un appel au `Dispose` d'une classe suivait tous les `Dispose` du plugin). Un délégué seulement gardé dans un champ du plugin
  (rangé, ou ajouté à une collection tenue par un champ, directement ou par les méthodes du plugin, comme une libération gardée pour
  `Dispose`) n'est suivi que si une méthode du chemin lit ce champ autrement que pour y ajouter ; un délégué donné à Dalamud par une aide
  du plugin n'est pas suivi. Mesuré sur Tensho Assistant : son constructeur était signalé par `NativeMapReplacer.Dispose` (seulement
  gardé pour la décharge) ; il l'est maintenant par un vrai appel de son constructeur (`CompetitiveWatcher.Decide → GameMain.IsInPvPInstance`).
- **Noms de `--run-anyway`.** Une fenêtre se nomme par son titre, son `###id` (avec ou sans `###`) ou son nom entier ; l'option peut se
  répéter ; `\,` garde une virgule dans un nom. L'indication du rapport donne un nom qui ne désigne que cette fenêtre (son `###id` quand
  deux fenêtres ont le même titre).
- Exemple : une quatrième fenêtre, dessinée par `samples/SampleLibrary` à côté du plugin ; la CI vérifie qu'elle est dessinée. Tests :
  l'analyse sur des cas types (libérations gardées, constructeur qui libère, délégué gardé puis rappelé, appels abstraits et d'interface)
  et les noms de `--run-anyway`.
- Rapport : les parties `load` et `framework` (genres ajoutés au schéma JSON, `schemaVersion` reste 1) disent `ran`. La console
  aligne `BLOCKED` et `CRASHED` sur les autres résultats ; les appels réécrits sont comptés à chaque passage, pas seulement le
  premier.

## 0.1.0

Première version : dessine chaque fenêtre d'un plugin Dalamud sans le jeu, dans un processus enfant, et dit ce qui a été dessiné, caché,
ce qui a levé une exception, bloqué, planté ou été sauté parce que son code peut atteindre la mémoire du jeu ; rapport JSON (schéma
version 1) ; exemple de CI.
