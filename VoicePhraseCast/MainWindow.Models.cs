using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace VoicePhraseCast
{
    /// <summary>
    /// Модели Vosk: первичная загрузка, список, смена, импорт папки.
    /// Нативной моделью владеет BridgeController. Здесь только интерфейс: смена модели не останавливает мост,
    /// а выбор сохраняется в настройках лишь после успешной загрузки.
    /// </summary>
    public partial class MainWindow
    {
        private bool _isUpdatingUi;   // подавляет SelectionChanged при программном заполнении списка

        // Вызывается один раз из Window_Loaded: скачивание при первом запуске, затем загрузка модели в фоне.
        private async Task LoadInitialModelAsync()
        {
            StatusText.Text = "Статус ИИ: подготовка модели...";

            string? modelPath = await _modelManager.EnsureModelAvailableAsync(status => PostUi(() => StatusText.Text = status));

            if (string.IsNullOrEmpty(modelPath))
            {
                TxtModelInfo.Text = "Модель Vosk не найдена и не скачана. Проверьте подключение к интернету или укажите папку с моделью кнопкой «Выбрать...».";
                StatusText.Text = "Статус ИИ: модель недоступна";
                LoadVoskModelsToUi();
                return;
            }

            await SwitchModelUiAsync(modelPath, showMessageBox: false);
        }

        // Загружает модель через контроллер. Не бросает исключений: при ошибке прежняя модель (если была) продолжает работать.
        private async Task<bool> SwitchModelUiAsync(string path, bool showMessageBox = true)
        {
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            CmbVoskModels.IsEnabled = false;
            TxtModelInfo.Text = $"Загрузка модели '{name}' в ОЗУ (это может занять время)...";

            try
            {
                await _controller.SwitchModelAsync(path);

                Cfg.SelectedModelPath = path;   // выбор сохраняется только после успешной загрузки
                ScheduleSave();

                TxtModelInfo.Text = $"Активная модель: {name}";
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Model] {ex}");

                string keep = _controller.CurrentModelPath != null ? " Продолжает работать прежняя модель." : "";
                TxtModelInfo.Text = $"Ошибка загрузки модели '{name}': {ex.Message}.{keep}";

                if (showMessageBox)
                {
                    System.Windows.MessageBox.Show($"Не удалось загрузить модель:\n{ex.Message}", "Ошибка ИИ",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                return false;
            }
            finally
            {
                LoadVoskModelsToUi();   // выбор в списке всегда совпадает с реально загруженной моделью
                CmbVoskModels.IsEnabled = true;
            }
        }

        private void LoadVoskModelsToUi()
        {
            _isUpdatingUi = true;
            try
            {
                CmbVoskModels.Items.Clear();

                var models = _modelManager.GetAvailableModels();
                string? current = _controller.CurrentModelPath
                                  ?? (string.IsNullOrWhiteSpace(Cfg.SelectedModelPath) ? null : Cfg.SelectedModelPath);

                // Текущая модель может лежать вне models/ (старые настройки): добавляем её в список, чтобы подпись не расходилась с реальностью.
                if (current != null && Directory.Exists(current) && !models.Contains(current, StringComparer.OrdinalIgnoreCase))
                    models.Insert(0, current);

                if (models.Count == 0)
                {
                    TxtModelInfo.Text = "Валидные модели не найдены в папке models/";
                    return;
                }

                int selected = 0;
                for (int i = 0; i < models.Count; i++)
                {
                    CmbVoskModels.Items.Add(new ComboBoxItem
                    {
                        Content = Path.GetFileName(Path.TrimEndingDirectorySeparator(models[i])),
                        Tag = models[i]   // полный путь хранится в Tag, а в списке показывается только имя папки
                    });

                    if (current != null && string.Equals(models[i], current, StringComparison.OrdinalIgnoreCase))
                        selected = i;
                }

                CmbVoskModels.SelectedIndex = selected;
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private async void CmbVoskModels_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi || CmbVoskModels.SelectedItem is not ComboBoxItem item || item.Tag is not string path) return;
            if (string.Equals(path, _controller.CurrentModelPath, StringComparison.OrdinalIgnoreCase)) return;

            await SwitchModelUiAsync(path);
        }

        private void BtnRefreshModels_Click(object sender, RoutedEventArgs e) => LoadVoskModelsToUi();

        private void BtnOpenModelsFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(_modelManager.ModelsDirectory);
                Process.Start(new ProcessStartInfo { FileName = _modelManager.ModelsDirectory, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Folder Error] Не удалось открыть папку: {ex.Message}");
            }
        }

        private async void BtnBrowseModelFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Выберите папку с моделью Vosk (внутри должны быть conf\\model.conf и am\\final.mdl)",
                Multiselect = false
            };
            if (dialog.ShowDialog() != true) return;

            string source = dialog.FolderName;
            if (!_modelManager.IsValidModelFolder(source))
            {
                System.Windows.MessageBox.Show(
                    "Выбранная папка не является валидной моделью Vosk!\n" +
                    "Внутри обязательно должны присутствовать файлы conf\\model.conf и am\\final.mdl.",
                    "Ошибка выбора модели", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                string target = _modelManager.GetImportTarget(source);
                string name = Path.GetFileName(target);

                // Модель уже лежит в models/: копировать нечего.
                bool alreadyInside = string.Equals(NormalizePath(source), NormalizePath(target), StringComparison.OrdinalIgnoreCase);

                if (!alreadyInside)
                {
                    bool overwrite = false;

                    if (Directory.Exists(target))
                    {
                        if (string.Equals(NormalizePath(target), NormalizePath(_controller.CurrentModelPath ?? ""), StringComparison.OrdinalIgnoreCase))
                        {
                            System.Windows.MessageBox.Show(
                                $"Модель '{name}' сейчас загружена, и перезаписать её нельзя. Выберите другую папку или переименуйте её.",
                                "Модель используется", MessageBoxButton.OK, MessageBoxImage.Information);
                            return;
                        }

                        var answer = System.Windows.MessageBox.Show(
                            $"Модель с именем '{name}' уже существует в папке models/. Перезаписать?",
                            "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
                        if (answer != MessageBoxResult.Yes) return;

                        overwrite = true;
                    }

                    CmbVoskModels.IsEnabled = false;
                    TxtModelInfo.Text = $"Копирование модели '{name}' в папку models/...";
                    await _modelManager.ImportModelAsync(source, target, overwrite);
                }

                await SwitchModelUiAsync(target);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Model Import] {ex}");
                TxtModelInfo.Text = $"Не удалось добавить модель: {ex.Message}";
                System.Windows.MessageBox.Show($"Не удалось добавить модель: {ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                LoadVoskModelsToUi();
                CmbVoskModels.IsEnabled = _controller.State != BridgeState.ReloadingModel;
            }
        }

        private static string NormalizePath(string path) =>
            string.IsNullOrEmpty(path) ? "" : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
