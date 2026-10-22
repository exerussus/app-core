# App-Core

UI-каркас приложения на UI Toolkit: машина загрузки, навигация по страницам, стек попапов,
самодостаточные скрины и безопасная зона.

## Структура

Папка = подсистема. Неймспейс зеркалит папку. Один публичный тип на файл, имя файла = имя типа.
Публичность выражается модификатором доступа, а не расположением, поэтому папок `API`,
`Abstractions`, `Models` и `Utils` здесь нет — в такие имена можно положить что угодно.

```
Runtime/
├── AppRunner.cs             хребет: инспектор, Awake/Update/OnDestroy, UI-слои
├── AppRunner.Boot.cs        машина загрузки и терминальные состояния
├── AppRunner.Navigation.cs  переходы между страницами
├── AppRunner.Popups.cs      стек попапов
├── AppRunner.Screens.cs     экран загрузки и оверлеи ошибок
├── AppRunner.SafeArea.cs    кадр и безопасная зона
├── AppRunner.Views.cs       обвязка кнопок вью
├── AppRunner.Overlay.cs     строка версии
├── AppRunner.Editor.cs      автопривязка ассетов из Assets/App/Settings (только редактор)
├── AppRunner.MainThread.cs  диспатчер главного потока
├── AppCorePaths.cs          фиксированная раскладка Assets/App и правила имён
├── Boot/                    AppBootstrapper, BootState, BootProgress, BootHaltException
├── Build/                   BuildInfo — версия, хэш, профиль, настройки строки версии
├── Navigation/              NavigatorService, NavigationSettings/Link, PageId, PopupId
├── Views/                   AppPage, AppPopup, AppFragment, контроллеры, ViewRoot, FullLayer/SafeLayer
├── Screens/                 AppScreen, Loading/Error/Critical + их контроллеры
├── Services/                IAppService, IAppServiceUpdate, IAppManipulatorBuilder, реестры
├── Signals/                 AppSignals (строковая шина), SignalButton
├── Input/                   InputAdapter
├── Layout/                  FramePolicy, ScreenMetrics, SafeAreaInsets
└── Internal/                BoundedStack
Editor/
├── AppCoreAssets.cs         единственные ассеты Settings: поиск, создание
├── Views/                   инспекторы вью, генерация контроллеров и .Elements.cs
├── Build/                   штамп BuildInfo вокруг билда, GitInfo, контракт шагов сборки
├── Text/                    наборы символов, запекание шрифтов, проверка перед билдом
└── Tools/                   Validate, Sync Views, Migrate to 4.0
```

`AppRunner` — один класс, разрезанный на партиалы по аспектам: поля живут рядом со своим аспектом,
а не свалены в общий блок.

## Ключевые понятия

**System Page/Popup** живёт внутри App: регистрируется в реестре, проходит DI-инъекцию, участвует
в навигации и стеке. Доступен только после того, как отработал соответствующий шаг загрузки.

**Screen** — самодостаточный оверлей. Определяющее свойство: скрин **никогда не касается
контейнера зависимостей**. Всё, что ему нужно, приходит из инспектора или аргументами `Show(...)`.
Поэтому он доступен на любом этапе загрузки — в том числе когда падение произошло в самой
регистрации DI и контейнера ещё нет. `LoadingScreen`, `ErrorScreen`, `CriticalScreen` — скрины.

**ErrorScreen** закрывается, и жизнь продолжается. **CriticalScreen** терминальный: из него
только перезапуск или выход.

## Машина загрузки

```
CoveringScreen → PreBootstrap → RegisteringServices → InitializingCore
               → InitializingServices → PostBootstrap → Ready
```

Плюс два терминальных состояния:

- **Failed** — шаг упал с исключением. Ядро показывает `CriticalScreen` с перезапуском/выходом.
- **Halted** — сервис бросил `BootHaltException` (гейт, регион-лок). Это не ошибка: инициатор уже
  показал свой скрин, поэтому ядро аварийный оверлей не рисует.

Порядок шагов не случаен: страницы и попапы регистрируются в `InitializingCore` **до** асинхронной
инициализации сервисов, чтобы сервис мог открыть попап из своего `Initialize`.

`stepTimeoutSeconds` включает watchdog на асинхронный шаг: зависший `await` превращается в видимый
`Failed`, а не в вечный экран загрузки. Ноль — выключено.

## Монтирование страниц

По умолчанию страница инстанцирует свой UXML при первом переходе на неё, а не на старте:
короче загрузка и меньше памяти на проектах с десятками экранов. Флаг `prewarmPages`
в инспекторе включает прогрев всех страниц сразу — он выполняется после инициализации
сервисов, чтобы событие `OnPageMounted` дошло до подписчиков.

Порядок жизни вью одинаков для страниц и попапов и не зависит от режима:

```
PreInitialize → реестр → инъекция зависимостей → Mount → BindElements() → Initialize()
→ манипуляторы → OnPageMounted → OnActivate
```

Зависимости приходят раньше вёрстки (контроллеру для них не нужен `Root`), а `Initialize`
вызывается уже после монтирования — то есть внутри него доступны и сервисы, и `Root`.

## Папка Assets/App

Папка целиком принадлежит AppCore, пути — константы `AppCorePaths`:

```
Assets/App/
├── Settings/      NavigationSettings.asset, BuildInfo.asset, TextSettings.asset, build-config.json
├── Pages/<Type>/  <id>.uxml, <Type>PageController.cs (+ .Elements.cs)
├── Popups/<Type>/ <id>.uxml, <Type>PopupController.cs (+ .Elements.cs)
├── Fragments/<Type>/ <id>.uxml, <Type>FragmentController.cs (+ .Elements.cs)
├── Styles/        Navigation.uss (генерируется), Tags.uss, AppStyle.uss
├── Containers/    шаблоны вёрстки: Default.uxml, Fragment.uxml, LoadingScreen.uxml
├── UIToolkit/     App Settings (PanelSettings), App Text Settings, AppTheme.tss
└── Generated/     NavTargets.cs
```

`NavigationSettings` существует в одном экземпляре; `AppRunner` получает ссылки на него
и на `BuildInfo` сам — поля в инспекторе заполняются редактором.

## Вью: жёсткая привязка к id

В инспекторе `AppPage` / `AppPopup` / `AppFragment` руками выбирается только id из
выпадающего списка (занятые другими вью id скрыты). Остальное выводится из него:

- имя GameObject = id;
- вёрстка = `Assets/App/<Вид>/<Type>/<id>.uxml` (кнопка «Создать», если её нет);
- контроллер = `<Type><Вид>Controller` на том же объекте — кнопка «Навесить» или
  «Создать и навесить» (генерация, компиляция, довешивание — само).

Контроллер `partial`: вторая половина `.Elements.cs` держит поля **всех** именованных
элементов uxml и перегенерируется при каждом импорте вёрстки. Привязка — до `Initialize`.
Повторяющиеся имена — ошибка валидатора. Проверка всего проекта — `Exerussus/App/Validate`.

## Вёрстка: один uxml со слоями

```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements" xmlns:ac="Exerussus.AppCore.Views">
    <ac:FullLayer> …фон, арт, диммер — до выреза… </ac:FullLayer>
    <ac:SafeLayer> …кнопки и текст — внутри безопасной зоны… </ac:SafeLayer>
</ui:UXML>
```

Любой слой можно опустить; вёрстка без слоёв целиком считается безопасной. Дерево
разворачивается прямо в корень вью, поэтому `Root.Q` видит оба слоя. Фрагменту слои не нужны:
полноэкранность решает хост, в котором он развёрнут.

## Сигналы

`AppSignals` — шина верхнего слоя: `Raise(id, arg)`, `Subscribe(id, handler)`, `SubscribeAll`.
Id и аргумент — строки: для обвязки и модульных событий этого хватает, на геймплей шина
не рассчитана. Кнопка поднимает сигнал классом `signal-button` (id = имя кнопки) или как
`<ac:SignalButton signal="shop.buy" arg="gold"/>`.

## Строка версии и BuildInfo

`BuildInfo` штампуется перед любым билдом (версия, хэш, профиль, платформа, время) и
откатывается после. `AppRunner` рисует строку версии в углу полосы кадра — режим, угол,
прозрачность и формат настраиваются в Nexus → App → Сборка → BuildInfo.

## Шаги сборки

Контракт пре-/пост-процессов пайплайна сборки (Nexus → App → Сборка) — в
`Exerussus.AppCore.Editor.Build`: `BuildStep<TSettings>` с `[BuildStep(id, name, stage)]`,
`BuildStepContext`, `BuildTokens`, `IBuildStepHost` (UI настроек в стиле окна, личные файлы
в UserSettings). Пайплайн находит шаги во всех сборках проекта, так что свой шаг может
лежать в любом пакете со ссылкой на `Exerussus.AppCore.Editor`: нет пакета — нет шага.
Подробности и правила — в шапке `Editor/Build/Pipeline/BuildSteps.cs`.

## Текст

`TextSettings` описывает наборы символов (кириллица и т.д.), сканирование исходников и цепочку
шрифтов. «Применить» в Nexus → App → Text прописывает настройки текста панели и допекает атласы;
перед билдом то же делается само, а символ вне всех шрифтов останавливает сборку.

## Локализация

Включается галочкой в `Assets/App/Settings/LocalizationSettings.asset` (меню Exerussus/App/Localization
Settings). Пока она снята, локализации нет вовсе: таблица не грузится, вёрстка не обходится, патчи не
принимаются, `Loc.Get` возвращает строку как есть.

- Ключ — строка с `^`: `text="^MENU_PLAY"` в uxml, `Loc.Get("^MENU_PLAY")` или `L.MENU_PLAY` в коде.
  Константы `L` генерируются в `Assets/App/Generated/LocKeys.cs` при каждом изменении таблицы.
- Таблица — один файл `.loc` (обычно `Assets/App/Localization/base.loc`), формат строковый:

  ```
  @language ru Русский plural=ru
  @language en English

  SCENARIO_COUNT
      ru.one: {0} сценарий
      ru.few: {0} сценария
      ru: {0} сценариев
      en.one: {0} scenario
      en: {0} scenarios
  ```

  Ключ — с начала строки, значения — с отступом; глубже — продолжение (перевод строки). `~` — удаление
  (в патче), `#` — комментарий, экранирование `\n \\ \s \~ \# \"`. Подробно — `LocFormat`.
- Аргументы: позиционные `{0}` и именованные `{count}` в одной строке.

  ```csharp
  Loc.Format("^HERO_POINTS_LEFT", ("count", 3));                    // без упаковки чисел (1–3 аргумента)
  Loc.Format("^SAVE_LOADED", ("name", world.Name), ("seconds", 1.25f));
  Text.Set(label, "^HERO_POINTS_LEFT", ("count", left));             // тот же count — повтор ничего не стоит
  ```

  Формат — после двоеточия, только из белого списка: стандартный (`N2`, `P0`, `F1` — буква и до двух
  цифр) или из `0 # . , % ‰ ; E e + -` и цифр (`0.0`, `#,##0`). Всё остальное в скобках — незнакомый токен
  и остаётся в строке как есть, даже если аргумент с таким именем передан: `{max:damage}`,
  `{max:ShieldGuardCost:a}` принадлежат проекту. Токен без аргумента остаётся без исключения и без лога
  (`WarnUnknownTokens` — предупреждать). `{имя|…}` зарезервировано под окончания внутри строки.
  `{{` и `}}` — буквальные скобки. Шаблон разбирается раз и кэшируется.
- Ключ без `^` (из скриптов, по сети): `Loc.GetKey("greet.friend")`, `TryGetKey`, `FormatKey`,
  `PluralKey` — тот же кэш, без склейки строк.
- Проходы (`ILocPass`) — доработка строки по ключу после перевода, подключаются как слои:
  `Loc.AddPass(id, pass, stage, owner, source)`, `RemovePass(es)`. Стадия `Pattern` — по шаблону до
  аргументов, результат кэшируется (род, подсветка, стиль речи); `Text` — по готовой строке на каждый
  показ (живые значения). `Wants` раз на шаблон сообщает зависимости строки; состояние сменилось —
  `Loc.RefreshPass(id)` или точечно `RefreshPass(id, "gold")`: пересобираются только строки с этой
  зависимостью, биндеры обновляют только их. Данные игрока и мода на стадии `Pattern` — через
  `Loc.Escape`. Проход мода (`LocSource.Mod`) подчиняется правилам слоёв модов (защищённые ключи,
  разметка). Упавший проход пропускается, лог — раз на ключ.
- Резолверы (`Loc.AddResolver`) — текст для ключа, которого нет ни в одном слое ни на одном языке
  (после фолбэков, до заглушки): `item.<id>.name` из каталога. Промахи кэшируются;
  `Loc.RefreshResolvers()` — после загрузки каталога.
- Выключить на запуск (сервер, тесты): `Loc.OverrideEnabled(false)` до первого обращения или
  `DisableInBatchMode` в настройках.
- Окончания: `Loc.Plural("^SCENARIO_COUNT", n)`; правило языка — `plural=` в заголовке или по коду
  (`ru`, `pl`, `cs`, `en`, `fr`, `none`, своё — `PluralRules.Register`).
- Страницы, попапы, фрагменты и скрины переводят свою вёрстку сами при монтировании и пересобирают
  её на смене языка. Строки из кода — `Text.Set(label, "^KEY", args)` в контроллере; свои подсчёты —
  в `OnLanguageChanged()`. Вне AppCore — свой `LocBinder` (его обязательно `Dispose`).
  `ILocTextProcessor` дорабатывает строку после перевода (живые значения проекта): `Prepare` — раз на
  перевод (разбор, подписки), `Build` — на изменение данных (склейка). Изменения собираются в одну очередь
  на биндер за кадр, неизменившийся видимый текст не пересобирается, `Suspend/Resume` — для скрытых
  деревьев. `ILocalizedElement` — составной элемент, который пересобирает часть текстов сам.
- `ILocValueCatalog` (редактор) — каталог живых значений проекта для конструктора строк в Nexus:
  список значений, форматы, построение токена и предпросмотр.
- Патчи: `Loc.AddLayer(id, table, source, owner)`, `AddLayerFromText`, `AddLayerFromFile`. Порядок
  добавления = порядок применения (позже — главнее), приоритетов нет; id задаёт вызывающий и обязан быть
  уникальным (`appcore` и `base` заняты). Моду по умолчанию нельзя удалять ключи, трогать защищённые
  ключи и (по настройке) ставить теги разметки. `Loc.Batch()` — пачка слоёв одной пересборкой.
- История: `Loc.History(key, lang)` — кто поставил, заменил, удалил, что отклонено и почему, откуда
  фолбэк. Пишется по настройке Trace (Off / Editor / DevelopmentBuilds / Always). То же в AppDeck
  (`loc.lang`, `loc.where`, `loc.history`, `loc.missing`, `loc.layers`, `loc.passes`, `loc.reload`) и в Nexus → App →
  Локализация → Патчи.
- Перед сборкой ключи кода и вёрстки сверяются с таблицей (Build Check: Off / Warn / Fail).

## Навигация «назад»

Семантика одна на оба входа — аппаратную кнопку (`InputAdapter.OnBackPressed`) и кнопку
в вёрстке с классом `to-back-page__navigation`:

1. открыт попап — закрывается верхний попап;
2. иначе поле страницы «Назад»: `Previous` — возврат по стеку, `ToPage` — переход на цель;
3. `None` — кнопка в вёрстке возвращает по стеку, аппаратная не делает ничего.

Курсор при входе на страницу — поле «Курсор» (`Keep` / `Lock` / `Unlock`).

Последний пункт намеренный: «назад» — осознанное свойство экрана, иначе системная кнопка
начала бы выкидывать пользователя с корневых страниц.

## Безопасная зона

Отступы выреза получает слой `SafeLayer` каждого вью. `AppRunner` держит реестр слоёв и
переприменяет отступы только когда реально изменились безопасная зона, ориентация или
разрешение: в обычном кадре это несколько сравнений структур и выход, без аллокаций.

## AppDeck

Панель приложения поверх всего — консоль, быстрые действия, метрики. Отдельная сборка
`Exerussus.AppCore.Deck`: ядро о ней не знает, проект подключает её ссылкой в asmdef.

- **Запуск.** Поднимается сама до первой сцены, если `AppDeckSettings.asset` (создаётся в
  `Assets/App/Settings` и кладётся в Preloaded Assets) разрешает: `Off` / `DevelopmentBuilds` / `Always`.
  Регистрация работает всегда — это просто данные. `Exerussus/App/AppDeck Settings` — открыть ассет.
- **Клавиши.** `` ` `` — открыть/закрыть, Shift + `` ` `` — шторка / весь экран, Esc — закрыть подсказки,
  потом окно.
- **Цель и аргументы извне.** AppDeck не знает, как выбирают сущности: проект сам ставит цель
  (`AppDeck.SetTarget`), подставляет токен в строку (`AppDeck.InsertArgument("#42")`) и разрешает токены
  резолвером (`AppDeck.RegisterTargetResolver`). `AppDeck.IsOverWindow(pos)` — клик пришёлся на окно, не на мир.
- **Ввод игры.** `AppDeck.BlocksInput` (открыто или закрылось этим же Esc), `AppDeck.IsTyping`,
  событие `AppDeck.OpenChanged`. Пока окно открыто, курсор свободен.

```csharp
// команда
AppDeck.Command("give")
    .Summary("Выдать предмет")
    .Args("item:string@item count:int=1")
    .AsAction("Выдать", "cheat, items")          // ещё и кнопкой во вкладке Actions
    .Run(ctx => Give(ctx.GetString("item"), ctx.GetInt("count")))
    .Register(owner);

AppDeck.Register("god", "on:bool=true", "Бессмертие", ctx => SetGod(ctx.GetBool(0)), owner);
AppDeck.RegisterSuggestions("item", itemIds, owner);          // подсказки для @item
AppDeck.AddAction("Золото +100", "give gold 100", "cheat", owner);
var kills = AppDeck.AddCounter("game.kills", "Game", track: true, owner: owner);
AppDeck.Count(kills);                                         // горячий путь: без аллокаций
AppDeck.AddGauge("game.agents", () => agents.Count, "Game", format: "F0", owner: owner);
AppDeck.RegisterTargetResolver(ResolveHero, owner);           // «me», «#42», имя → цель для target
AppDeck.UnregisterAll(owner);                                 // снять всё разом
```

Регистрируйте не раньше `BeforeSceneLoad`: на `SubsystemRegistration` реестры сбрасываются.
