using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace VoicePhraseCast
{
    /// <summary>
    /// Поиск, проверка и скачивание моделей Vosk. Не создаёт Vosk.Model: владение нативной моделью лежит на BridgeController.
    /// Конструктор ничего не делает с диском; миграцию старой папки model/ выполняет RunMigrationAsync (вне UI-потока).
    /// </summary>
    public class ModelManager
    {
        public const string DefaultModelName = "vosk-model-small-ru-0.22";
        private const string DefaultModelUrl = "https://alphacephei.com/vosk/models/vosk-model-small-ru-0.22.zip";

        private readonly string _modelsDirectory;
        private readonly string _legacyModelDirectory;

        public ModelManager()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _modelsDirectory = Path.Combine(baseDir, "models");
            _legacyModelDirectory = Path.Combine(baseDir, "model");
        }

        public string ModelsDirectory => _modelsDirectory;

        /// <summary>
        /// Создаёт папку models/ и переносит валидную legacy-папку model/ внутрь неё.
        /// Ничего не удаляется: если целевая папка занята, старая получает имя model.migrated-{время}, и пользователь удалит её сам.
        /// </summary>
        public Task RunMigrationAsync() => Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(_modelsDirectory);

                if (!Directory.Exists(_legacyModelDirectory) || !IsValidModelFolder(_legacyModelDirectory))
                    return;

                string target = Path.Combine(_modelsDirectory, DefaultModelName);
                if (!Directory.Exists(target))
                {
                    Directory.Move(_legacyModelDirectory, target);
                    Debug.WriteLine($"[ModelManager] Модель из model/ перенесена в: {target}");
                }
                else
                {
                    string backup = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                                 $"model.migrated-{DateTime.Now:yyyyMMddHHmmss}");
                    Directory.Move(_legacyModelDirectory, backup);
                    Debug.WriteLine($"[ModelManager] Папка model/ переименована в {backup}: ничего не удалено.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ModelManager Migration Error] {ex.Message}");
            }
        });

        /// <summary>Сканирует models/ и возвращает пути ко всем валидным моделям.</summary>
        public List<string> GetAvailableModels()
        {
            var valid = new List<string>();
            if (!Directory.Exists(_modelsDirectory)) return valid;

            try
            {
                foreach (string dir in Directory.GetDirectories(_modelsDirectory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    // Служебные папки скачивания и импорта могут остаться после аварийного завершения и выглядеть как валидная модель.
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith('.') || name.Equals("temp_model", StringComparison.OrdinalIgnoreCase)) continue;

                    if (IsValidModelFolder(dir)) valid.Add(dir);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ModelManager Scan Error] {ex.Message}");
            }

            return valid;
        }

        /// <summary>
        /// Папка считается моделью, если есть conf/model.conf и am/final.mdl. Одной проверки наличия папок conf и am мало:
        /// недокачанная модель их уже содержит, а Vosk.Model на ней падает.
        /// </summary>
        public bool IsValidModelFolder(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return false;

            return File.Exists(Path.Combine(folderPath, "conf", "model.conf"))
                && File.Exists(Path.Combine(folderPath, "am", "final.mdl"));
        }

        /// <summary>
        /// Возвращает путь к модели, которую можно загружать: сохранённая в настройках, первая из models/ или свежескачанная.
        /// Не бросает исключений: при сбое сети или распаковки возвращает null, а причину передаёт в statusCallback.
        /// Настройка SelectedModelPath здесь НЕ записывается: её сохраняет владелец после успешной загрузки Vosk.Model.
        /// </summary>
        public async Task<string?> EnsureModelAvailableAsync(Action<string>? statusCallback = null)
        {
            try
            {
                await RunMigrationAsync().ConfigureAwait(false);

                string saved = Properties.Settings.Default.SelectedModelPath;
                if (!string.IsNullOrWhiteSpace(saved) && IsValidModelFolder(saved))
                    return saved;

                var available = GetAvailableModels();
                if (available.Count > 0)
                    return available[0];

                string target = Path.Combine(_modelsDirectory, DefaultModelName);
                await DownloadDefaultModelAsync(target, statusCallback).ConfigureAwait(false);

                return IsValidModelFolder(target) ? target : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ModelManager] {ex}");
                statusCallback?.Invoke($"Не удалось подготовить модель: {ex.Message}");
                return null;
            }
        }

        private async Task DownloadDefaultModelAsync(string targetFolder, Action<string>? statusCallback)
        {
            Directory.CreateDirectory(_modelsDirectory);

            string zipPath = Path.Combine(_modelsDirectory, "vosk_model.zip");
            string tempExtractPath = Path.Combine(_modelsDirectory, "temp_model");

            statusCallback?.Invoke("Модели отсутствуют. Скачивание базовой модели (около 50 МБ)...");

            try
            {
                // Таймаут по умолчанию (100 с) обрывал бы медленную загрузку, а ResponseHeadersRead не буферизует архив в памяти.
                using (var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
                using (var response = await client.GetAsync(DefaultModelUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    long? total = response.Content.Headers.ContentLength;

                    await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    await using var destination = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None,
                                                                 bufferSize: 81920, useAsync: true);

                    var buffer = new byte[81920];
                    long done = 0;
                    int read;
                    int lastPercent = -1;
                    while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false)) > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                        done += read;

                        if (total is > 0)
                        {
                            int percent = (int)(done * 100 / total.Value);
                            if (percent != lastPercent && percent % 5 == 0)
                            {
                                lastPercent = percent;
                                statusCallback?.Invoke($"Скачивание модели: {percent}%");
                            }
                        }
                    }
                }

                statusCallback?.Invoke("Распаковка модели ИИ...");

                // Распаковка и перенос идут в пуле потоков, а не в UI-потоке после await.
                await Task.Run(() =>
                {
                    if (Directory.Exists(tempExtractPath)) Directory.Delete(tempExtractPath, true);
                    ZipFile.ExtractToDirectory(zipPath, tempExtractPath);

                    string[] extracted = Directory.GetDirectories(tempExtractPath);
                    string inner = extracted.Length > 0
                        ? extracted[0]
                        : throw new InvalidDataException("Архив модели пуст или имеет неожиданную структуру.");

                    if (Directory.Exists(targetFolder)) Directory.Delete(targetFolder, true);
                    Directory.Move(inner, targetFolder);
                }).ConfigureAwait(false);
            }
            finally
            {
                // Временные файлы убираются при любом исходе, чтобы битый архив не оставался на диске.
                TryDelete(() => { if (File.Exists(zipPath)) File.Delete(zipPath); });
                TryDelete(() => { if (Directory.Exists(tempExtractPath)) Directory.Delete(tempExtractPath, true); });
            }
        }

        /// <summary>Путь внутри models/, куда будет скопирована модель из sourceFolder (имя папки сохраняется).</summary>
        public string GetImportTarget(string sourceFolder) =>
            Path.Combine(_modelsDirectory, Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceFolder)));

        /// <summary>
        /// Копирует модель в models/ через временную папку: недокопированная модель не появляется в списке,
        /// а при сбое (нет места, файл занят) прежняя модель с тем же именем остаётся нетронутой до самого переноса.
        /// </summary>
        public Task ImportModelAsync(string sourceFolder, string targetFolder, bool overwrite) => Task.Run(() =>
        {
            Directory.CreateDirectory(_modelsDirectory);
            string staging = Path.Combine(_modelsDirectory, ".import-" + Guid.NewGuid().ToString("N"));

            try
            {
                CopyDirectory(sourceFolder, staging);
                if (!IsValidModelFolder(staging))
                    throw new InvalidDataException("Копия модели получилась неполной.");

                if (Directory.Exists(targetFolder))
                {
                    if (!overwrite) throw new IOException("Папка модели с таким именем уже существует.");
                    Directory.Delete(targetFolder, true);
                }

                Directory.Move(staging, targetFolder);
            }
            finally
            {
                TryDelete(() => { if (Directory.Exists(staging)) Directory.Delete(staging, true); });
            }
        });

        private static void CopyDirectory(string sourceDir, string destinationDir)
        {
            Directory.CreateDirectory(destinationDir);

            foreach (string file in Directory.GetFiles(sourceDir))
                File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), true);

            foreach (string sub in Directory.GetDirectories(sourceDir))
                CopyDirectory(sub, Path.Combine(destinationDir, Path.GetFileName(sub)));
        }

        private static void TryDelete(Action delete)
        {
            try
            {
                delete();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ModelManager Cleanup] {ex.Message}");
            }
        }
    }
}
