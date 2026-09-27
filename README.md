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
├── Build/                   штамп BuildInfo вокруг билда, GitInfo
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

## Текст

`TextSettings` описывает наборы символов (кириллица и т.д.), сканирование исходников и цепочку
шрифтов. «Применить» в Nexus → App → Text прописывает настройки текста панели и допекает атласы;
перед билдом то же делается само, а символ вне всех шрифтов останавливает сборку.

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
