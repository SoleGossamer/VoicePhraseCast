using System.Configuration;

namespace VoicePhraseCast.Properties
{
    /// <summary>
    /// Новые настройки версии с BridgeController. Лежат в отдельной части partial-класса, а не в Settings.settings:
    /// так не нужно подменять Settings.Designer.cs (его пересоздаёт Visual Studio), а уже сохранённые настройки
    /// пользователя (user.config) остаются на месте. Значения по умолчанию задаются атрибутами.
    /// Модификаторы доступа не указаны намеренно: они берутся из сгенерированной части класса.
    /// </summary>
    partial class Settings
    {
        // ---- устройства (выбор запоминается по имени, а не по индексу) ----

        [UserScopedSetting]
        [DefaultSettingValue("")]
        public string MicDevice
        {
            get => (string)this[nameof(MicDevice)];
            set => this[nameof(MicDevice)] = value;
        }

        [UserScopedSetting]
        [DefaultSettingValue("")]
        public string CableDevice
        {
            get => (string)this[nameof(CableDevice)];
            set => this[nameof(CableDevice)] = value;
        }

        [UserScopedSetting]
        [DefaultSettingValue("")]
        public string MonitorDevice
        {
            get => (string)this[nameof(MonitorDevice)];
            set => this[nameof(MonitorDevice)] = value;
        }

        // ---- уровень фраз в игру ----

        /// <summary>
        /// Потолок пика фраз в игру, dBFS (−24…0, шаг ползунка 1 дБ). Заменяет линейную «Громкость в игру» (VolumeCable):
        /// громкость всех фраз выравнивается под него (тихие подтягиваются, плотные приглушаются), пик обычной речи ложится на этот
        /// уровень, и ни один отсчёт не оказывается выше. То же выравнивание действует в наушниках («для себя»), ползунок
        /// VolumeMic там остаётся подстройкой относительно выровненного уровня (см. ReferenceCrestDb — плотность выравнивания).
        /// </summary>
        [UserScopedSetting]
        [DefaultSettingValue("-3")]
        public double TargetPeakDb
        {
            get => (double)this[nameof(TargetPeakDb)];
            set => this[nameof(TargetPeakDb)] = value;
        }

        // ---- диктовка (STT) ----

        /// <summary>Глушить микрофон в игре, пока идёт диктовка: союзники не слышат процесс.</summary>
        [UserScopedSetting]
        [DefaultSettingValue("True")]
        public bool MuteMicDuringStt
        {
            get => (bool)this[nameof(MuteMicDuringStt)];
            set => this[nameof(MuteMicDuringStt)] = value;
        }

        [UserScopedSetting]
        [DefaultSettingValue("True")]
        public bool SttAutoPaste
        {
            get => (bool)this[nameof(SttAutoPaste)];
            set => this[nameof(SttAutoPaste)] = value;
        }

        [UserScopedSetting]
        [DefaultSettingValue("True")]
        public bool SttOpenChat
        {
            get => (bool)this[nameof(SttOpenChat)];
            set => this[nameof(SttOpenChat)] = value;
        }

        [UserScopedSetting]
        [DefaultSettingValue("True")]
        public bool SttUseClipboard
        {
            get => (bool)this[nameof(SttUseClipboard)];
            set => this[nameof(SttUseClipboard)] = value;
        }

        [UserScopedSetting]
        [DefaultSettingValue("True")]
        public bool SttSendEnter
        {
            get => (bool)this[nameof(SttSendEnter)];
            set => this[nameof(SttSendEnter)] = value;
        }

        // ---- тонкая настройка (в интерфейсе не выводится, меняется в user.config) ----

        /// <summary>Журнал нераспознанных фраз: %LocalAppData%\VoicePhraseCast\missing_phrases.txt.</summary>
        [UserScopedSetting]
        [DefaultSettingValue("True")]
        public bool LogMissingPhrases
        {
            get => (bool)this[nameof(LogMissingPhrases)];
            set => this[nameof(LogMissingPhrases)] = value;
        }

        /// <summary>Пауза между нажатием рации и началом звука, мс (0–1000). Подбирается под вашу игру.</summary>
        [UserScopedSetting]
        [DefaultSettingValue("150")]
        public int RadioWarmupMs
        {
            get => (int)this[nameof(RadioWarmupMs)];
            set => this[nameof(RadioWarmupMs)] = value;
        }

        /// <summary>
        /// Глубина очереди вывода, мс (100–300; меньшее значение подтягивается к 100, большее к 300). Больше — устойчивее звук под нагрузкой от игры,
        /// но позже он слышен. Если заикания прошли с запасом, можно пробовать 100; если остались, поднимайте до 160–200.
        /// </summary>
        [UserScopedSetting]
        [DefaultSettingValue("120")]
        public int OutputLatencyMs
        {
            get => (int)this[nameof(OutputLatencyMs)];
            set => this[nameof(OutputLatencyMs)] = value;
        }

        /// <summary>
        /// Опорный запас «пик над громкостью», дБ: насколько плотно выравниваются фразы по громкости (допустимо 8–24, за границей
        /// значение подтягивается к ближайшей). Громкость, до которой подтягиваются все фразы, равна «потолок пика с ползунка − это значение»,
        /// а выбросы выше «громкость + это значение» (щелчок в шёпоте, взрывные согласные) срезаются при подготовке фразы:
        ///   15      — стандарт: обычная речь звучит естественно, срезается только редкий выброс;
        ///   10–12   — плотнее и громче: пики срезаются сильнее, фразы выходят заметно громче при том же потолке, звук чуть «прижатее»;
        ///   18      — мягче: больше живой динамики, фразы тише при том же потолке.
        /// Правится в user.config при закрытом приложении (дробные значения через точку), действует после запуска моста;
        /// фразы, уже лежащие в кэше, при смене значения готовятся заново.
        /// </summary>
        [UserScopedSetting]
        [DefaultSettingValue("15")]
        public double ReferenceCrestDb
        {
            get => (double)this[nameof(ReferenceCrestDb)];
            set => this[nameof(ReferenceCrestDb)] = value;
        }
    }
}
