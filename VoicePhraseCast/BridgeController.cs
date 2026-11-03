using System.Diagnostics;
using Vosk;

namespace VoicePhraseCast
{
    /// <summary>
    /// Состояния моста. ReloadingModel — идёт подмена модели Vosk: сам мост (если он был запущен) продолжает работать
    /// на прежней модели, а по окончании подмены состояние возвращается к Stopped или Running.
    /// </summary>
    public enum BridgeState
    {
        Stopped,
        Starting,
        Running,
        Stopping,
        ReloadingModel
    }

    /// <summary>Параметры запуска: индексы устройств NAudio и коды клавиш (в том числе 0x05/0x06 — боковые кнопки мыши), которые слушает хук.</summary>
    public sealed record BridgeConfig(int MicIndex, int CableIndex, int MonitorIndex, IReadOnlyCollection<int> WatchedKeys);

    /// <summary>
    /// Единственный владелец жизненного цикла: Vosk.Model, AudioProcessor, SpeechToTextService и InputHook.
    /// Все переходы (запуск, остановка, смена модели, выход) сериализуются одним асинхронным замком _gate,
    /// поэтому окно не может ни запустить мост поверх остановки, ни освободить модель под работающим распознавателем.
    ///
    /// Правила:
    ///  • методы можно вызывать из UI-потока: внутри везде ConfigureAwait(false), блокирующего ожидания нет;
    ///  • события StateChanged и Faulted приходят из потоков пула (подписчик сам идёт в Dispatcher) и никогда не вызываются под блокировкой потока;
    ///  • Vosk.Model освобождается только после того, как оба распознавателя (STT и фразы) пересозданы на новой модели;
    ///  • сбой при подмене модели оставляет прежнюю модель и прежнее состояние.
    /// </summary>
    public sealed class BridgeController
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private volatile BridgeState _state = BridgeState.Stopped;
        private Model? _model;
        private volatile string? _modelPath;
        private int _shutdown;

        public AudioProcessor Audio { get; }
        public SpeechToTextService Stt { get; } = new();
        public InputHook Hook { get; } = new();

        public BridgeController()
        {
            Audio = new AudioProcessor { Stt = Stt };
            Audio.DeviceFailed += OnDeviceFailed;
        }

        public BridgeState State => _state;

        /// <summary>Путь к загруженной в данный момент модели (null, пока модель не загружена).</summary>
        public string? CurrentModelPath => _modelPath;

        public event Action<BridgeState>? StateChanged;

        /// <summary>Мост остановлен аварийно (отключили микрофон, исчезло устройство вывода). К моменту события состояние уже Stopped.</summary>
        public event Action<string>? Faulted;

        // ================= запуск и остановка =================

        /// <summary>Запускает устройства, затем хук. Если любой шаг не удался — всё, что успело запуститься, откатывается, исключение уходит вызывающему.</summary>
        public async Task StartAsync(BridgeConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                ThrowIfShutdown();
                if (_state != BridgeState.Stopped) return;   // внутри замка бывают только Stopped и Running

                SetState(BridgeState.Starting);
                try
                {
                    await Audio.StartAsync(config.MicIndex, config.CableIndex, config.MonitorIndex).ConfigureAwait(false);

                    try
                    {
                        Hook.Start(config.WatchedKeys);
                    }
                    catch
                    {
                        // Хук не встал: без хоткеев мост бесполезен, а кабель не должен остаться занятым.
                        await Audio.StopAsync().ConfigureAwait(false);
                        throw;
                    }

                    SetState(BridgeState.Running);
                }
                catch
                {
                    SetState(BridgeState.Stopped);
                    throw;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task StopAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        // Только под _gate.
        private async Task StopCoreAsync()
        {
            if (_state != BridgeState.Running) return;

            SetState(BridgeState.Stopping);
            try
            {
                // Сначала хук: новые нажатия больше не приходят, пока гасятся устройства и отпускается рация.
                await Task.Run(Hook.Stop).ConfigureAwait(false);
                await Audio.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Bridge] Остановка: {ex}");
            }
            finally
            {
                SetState(BridgeState.Stopped);
            }
        }

        // ================= модель Vosk =================

        /// <summary>
        /// Загружает модель и подменяет ею распознаватели STT и фраз. Мост при этом не останавливается:
        /// пока идёт загрузка, хоткеи работают на прежней модели. При ошибке прежняя модель остаётся нетронутой.
        /// </summary>
        public async Task SwitchModelAsync(string modelPath, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
                throw new ArgumentException("Не указан путь к модели.", nameof(modelPath));

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            BridgeState previous = _state;
            try
            {
                ThrowIfShutdown();
                SetState(BridgeState.ReloadingModel);

                // 1. Новая модель грузится в фоне, старая всё это время работает.
                Model fresh = await Task.Run(() =>
                {
                    Vosk.Vosk.SetLogLevel(-1);
                    return new Model(modelPath);
                }, ct).ConfigureAwait(false);

                // 2. Подмена распознавателей. Если сбой случился до первой подмены, новая модель никому не нужна и освобождается.
                //    Если после первой, на неё уже может ссылаться живой распознаватель: освобождать нельзя (утечка ~50 МБ лучше падения нативного кода).
                bool swapped = false;
                try
                {
                    await Task.Run(() => Stt.Initialize(fresh), ct).ConfigureAwait(false);
                    swapped = true;
                    await Audio.AttachModelAsync(fresh).ConfigureAwait(false);
                }
                catch
                {
                    if (!swapped) fresh.Dispose();
                    throw;
                }

                // 3. Оба старых распознавателя уже освобождены внутри Initialize/AttachModelAsync, теперь безопасно выгрузить старую модель.
                Model? old = _model;
                _model = fresh;
                _modelPath = modelPath;
                if (old != null) await Task.Run(old.Dispose).ConfigureAwait(false);
            }
            finally
            {
                SetState(previous);
                _gate.Release();
            }
        }

        // ================= аварии устройств =================

        private void OnDeviceFailed(string message)
        {
            // Событие пришло из потока записи NAudio или таймера watchdog: останавливаться нужно в другом потоке.
            _ = Task.Run(async () =>
            {
                try
                {
                    await StopAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Bridge] Остановка после сбоя устройства: {ex.Message}");
                }

                try
                {
                    Faulted?.Invoke(message);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Bridge] Faulted: {ex.Message}");
                }
            });
        }

        // ================= завершение работы =================

        /// <summary>
        /// Полная остановка при выходе: хук, устройства (с отпусканием рации), распознаватели, модель.
        /// Дожидается текущей операции (например, загрузки модели). Повторный вызов ничего не делает.
        /// </summary>
        public async Task ShutdownAsync()
        {
            if (Interlocked.Exchange(ref _shutdown, 1) == 1) return;

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopCoreAsync().ConfigureAwait(false);

                await Task.Run(() =>
                {
                    Hook.Dispose();
                    Audio.Dispose();   // освобождает свой распознаватель и отпускает рацию; модель не трогает
                    Stt.Dispose();
                }).ConfigureAwait(false);

                Model? model = _model;
                _model = null;
                _modelPath = null;
                if (model != null) await Task.Run(model.Dispose).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private void ThrowIfShutdown()
        {
            if (Volatile.Read(ref _shutdown) == 1)
                throw new ObjectDisposedException(nameof(BridgeController));
        }

        private void SetState(BridgeState state)
        {
            if (_state == state) return;
            _state = state;

            try
            {
                StateChanged?.Invoke(state);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Bridge] StateChanged: {ex.Message}");
            }
        }
    }
}
