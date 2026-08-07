# Changelog

Формат — [Keep a Changelog](https://keepachangelog.com/ru/1.1.0/),
версии — [Semantic Versioning](https://semver.org/lang/ru/).

## [2.1.1]

### Исправлено

- `DrainMainThreadQueueOnDestroy` не компилировался: при переводе очереди на переиспользуемый
  буфер заменились объявление и заголовок цикла, а тело осталось обращаться к удалённой
  локальной переменной `snapshot`. Сборка 2.1.0 нерабочая, используйте эту.

## [2.1.0]

### Добавлено

- `prewarmPages` — прогрев вёрстки всех страниц на старте. По умолчанию **выключен**:
  страница инстанцирует свой UXML при первом переходе на неё. Прогрев выполняется после
  инициализации сервисов, поэтому событие о монтировании получают все страницы.

### Исправлено

- **`OnPageMounted` не вызывался никогда.** Все страницы монтировались в `InitializingCore`,
  поэтому в навигации `Mount` всегда возвращал `false`, а сервисы подписывались на событие
  уже после этого. Следствие: `NavigatorService` не регистрировал ни back-хуки, ни настройки
  курсора — аппаратная кнопка «назад» и `cursor-lock`/`cursor-unlock` не работали вообще.
- **Кнопка «назад» не видела попапы.** Аппаратный back уводил страницу под открытым попапом,
  оставляя попап висеть. Семантика «назад» теперь одна на оба входа (аппаратная кнопка и
  кнопка в вёрстке): верхний попап → хук страницы → стек переходов.
- **`Initialize()` контроллера вызывался без `Root`.** У попапов — всегда (они монтируются
  лениво, а `Initialize` шёл на старте), у страниц — стал бы так же при ленивом монтировании.
  Теперь `Initialize` вызывается один раз сразу после монтирования, когда уже есть и
  зависимости, и вёрстка. Страницы и попапы ведут себя одинаково.
- **Контроллеры попапов не получали зависимости.** Инъекция шла в сам `AppPopup`, но не в его
  контроллер. Теперь инъецируются и вью, и контроллер — для страниц и попапов одинаково.
- **Дубликат uss-класса в `NavigationSettings` ронял старт.** `Add` бросал исключение вопреки
  комментарию про «last write wins»; заменено на присваивание по индексатору.
- **Дублирующиеся id страниц/попапов** давали нечитаемое «An item with the same key has already
  been added». Теперь ошибка называет id и оба конфликтующих объекта.

### Производительность

- Обход `NavigationLink.LinksPages`/`LinksPopups` больше не боксит энумератор: словари
  отдаются конкретным типом, а не через `IReadOnlyDictionary`. Это горячий путь — обход идёт
  на каждую кнопку каждой вью.
- `NavigationLink.Initialize` обходит `Entries` по индексу вместо `foreach` по `IReadOnlyList`.
- Диспатчер главного потока сливает очередь в переиспользуемый буфер вместо `ToArray()`
  на каждом кадре с работой.
- Кадровый путь `Update` проверен целиком и не аллоцирует: все три тика выходят по раннему 
  возврату, сервисы обходятся `foreach` по массиву.

## [2.0.1]

Только исправления найденных багов, структура не менялась.

### Исправлено

- **Попапы не получали манипуляторы.** `AppPopup` не был `IAppView` и никогда не проходил
  через `RegisterAppView`, поэтому внутри попапов не работали ни звуки UI, ни навигация
  по uss-классам. Теперь попап регистрируется при первом монтировании — это и есть его
  первая активация. Заодно у попапа появилось своё поле `overrideSoundLibrary`,
  а мёртвое поле `_registered` удалено.
- **Навигация зависела от библиотеки звуков.** Весь проход по кнопкам в `RegisterAppView`
  стоял под `if (soundLibrary != null)`, хотя в том же цикле строятся навигационные
  манипуляторы: без назначенной `UISoundLibrary` молча переставали работать переходы
  по классам. Решение «есть ли звук» теперь принимает `PageSoundService` внутри себя.
- **`OverrideSoundLibrary` ничего не переопределял.** Поле читалось в `RegisterAppView`
  и использовалось только как проверка на null, в сервис не передавалось.
  Теперь `PageSoundService` берёт библиотеку вью, а общую использует как запасную.
- **`PayloadBuilder.End()` не отпускал пейлоад.** Поле не обнулялось, поэтому следующая
  кнопка получала из `CreatePayload()` тот же самый пейлоад и перезаписывала уже
  проставленную цель навигации. Скоуп теперь закрывается на каждой кнопке, а невостребованный
  пейлоад освобождается.
- **Экран загрузки зависал при `timeScale = 0`.** `FadeTo` крутился на `Time.deltaTime`;
  переведён на `Time.unscaledDeltaTime`.
- Убран отладочный вывод из рантайма: `SoundClickManipulator` логировал создание на каждую
  кнопку каждой страницы, `AppPage`/`AppPopup` — на каждую загруженную вью, `UISignal` —
  на старте.

## [2.0.0]

Реструктуризация пакета. Логика не менялась, кроме раздела «Исправлено».
Пошаговый перенос — в [MIGRATION.md](MIGRATION.md).

### Изменено (ломающие)

- Папки названы по подсистемам вместо `API` / `Abstractions` / `Models` / `Utils`.
  Неймспейсы зеркалят папки, корень — `Exerussus.AppCore`.
- Сборки переименованы: `app.core` → `Exerussus.AppCore`, `app.core.editor` →
  `Exerussus.AppCore.Editor`. Заполнен `rootNamespace`.
- `PageUID` → `PageId`, `PopupUID` → `PopupId`.
- `AppServiceRegister` → `AppServiceRegistry`, `InternalServicesRegister` →
  `InternalServiceRegistry`, `NavigationSetting` → `NavigationSettings`,
  `UiSignal` → `UISignal`, `AudioPageService` → `PageSoundService`,
  `SafeAreaUtility` → `SafeAreaLayout`, `NavigationDropdownDrawer` → `NavigationIdDrawer`.
- `BootState` и `BootProgress` вынесены из `AppRunner` в `Exerussus.AppCore.Boot`;
  `BootState` стал публичным.
- `BootProgress.Stage` (string) заменён на `BootProgress.State` (`BootState`) — строка
  требовала `ToString()` на каждом переходе, то есть аллокацию на ровном месте.
- Сериализованные поля `AppRunner`: `navigationSetting` → `navigationSettings`,
  `appServiceRegister` → `appServiceRegistry`. Оба помечены `[FormerlySerializedAs]`,
  ссылки в сценах не теряются.

### Исправлено

- `UISoundLibrary`: `using UnityEditor;` вынесен под `#if UNITY_EDITOR`. В прежнем виде
  сборка плеера падала на этом using.
- `Exerussus.AppCore.Editor` теперь собирается только под редактор
  (`includePlatforms: ["Editor"]`), а не попадает в плеер.
- `UISignal`: вызов хост-пакета записан как `global::Signals.Signal`. Без квалификатора
  идентификатор `Signals` разрешался бы в собственный неймспейс `Exerussus.AppCore.Signals`.

### Внутреннее

- `AppRunner` разрезан на партиалы по аспектам: `Boot`, `Navigation`, `Popups`, `Screens`,
  `SafeArea`, `Views`, `MainThread`. Поля переехали к своему аспекту.
- Один публичный тип на файл: разделены `IAppService` / `IAppServiceUpdate` /
  `IAppManipulatorBuilder`, `SignalClickManipulator` / `ButtonPressed`,
  `SafeAreaLayout` / `SafeAreaInsets`, `NavigationData` и dropdown-атрибуты.
- Убраны мусорные и неиспользуемые `using`, включая алиас `AppPopup`.
