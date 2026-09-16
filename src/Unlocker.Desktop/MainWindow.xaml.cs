using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Unlocker.Core;
using Unlocker.Infrastructure;

namespace Unlocker.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly RecoveryEngine _engine;
    private readonly IPolicyRegistry _registry;
    private RecoveryRecord? _latestUndoable;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        Title = "SimpleUnlocker Next — Foundation";
        _registry = new WindowsPolicyRegistry();
        _engine = new RecoveryEngine(_registry, new FileRecoveryJournal());
        try { UpdateHistory(); }
        catch (Exception ex)
        {
            HistoryText.Text = "Не удалось прочитать журнал восстановления. Проверьте файлы вручную.";
            Show("Ошибка журнала", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync(async () =>
        {
            var findings = await Task.Run(() => RestrictionScanner.Scan(_registry));
            ResultsList.ItemsSource = findings.Select(f => new PolicyItem(f)).ToList();
            SelectedTitle.Text = "Выберите результат";
            SelectedDetails.Text = "Исправление не выполняется автоматически.";
            Show("Сканирование завершено", $"Проверено: {findings.Count}. Ограничений: {findings.Count(f => f.State == RestrictionState.Restricted)}.", InfoBarSeverity.Success);
        });
    }

    private async void RepairButton_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not PolicyItem selected ||
            selected.Finding.State != RestrictionState.Restricted) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Подтвердить исправление?",
            Content = "Будет удалено только выбранное значение политики HKEY_CURRENT_USER. Исходное значение сохранится в журнале. Ограничение может быть установлено администратором намеренно.",
            PrimaryButtonText = "Исправить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await ExecuteAsync(async () =>
        {
            var record = await Task.Run(() => _engine.RepairAsync(selected.Finding.Rule.Id));
            Show("Исправлено", $"Создана резервная копия {record.Id:N}.", InfoBarSeverity.Success);
            ResultsList.ItemsSource = RestrictionScanner.Scan(_registry).Select(f => new PolicyItem(f)).ToList();
            UpdateHistory();
        });
    }

    private async void RollbackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_latestUndoable is not RecoveryRecord record) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Вернуть исходное ограничение?",
            Content = "Это снова включит ранее снятое ограничение. Другие изменения реестра не затрагиваются.",
            PrimaryButtonText = "Откатить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await ExecuteAsync(async () =>
        {
            await Task.Run(() => _engine.RollbackAsync(record.Id));
            Show("Откат выполнен", "Исходное значение политики восстановлено.", InfoBarSeverity.Success);
            ResultsList.ItemsSource = RestrictionScanner.Scan(_registry).Select(f => new PolicyItem(f)).ToList();
            UpdateHistory();
        });
    }

    private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is PolicyItem item)
        {
            SelectedTitle.Text = item.Title;
            SelectedDetails.Text = item.Finding.Rule.Description + "\n\n" +
                "Реестр: HKCU\\" + item.Finding.Rule.RegistrySubKey + "\\" + item.Finding.Rule.ValueName +
                "\nСостояние: " + item.StateText +
                (item.Finding.Diagnostic is null ? "" : "\nОшибка чтения: " + item.Finding.Diagnostic);
        }
        RepairButton.IsEnabled = !_busy && ResultsList.SelectedItem is PolicyItem selected &&
                                 selected.Finding.State == RestrictionState.Restricted;
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        ScanButton.IsEnabled = RepairButton.IsEnabled = RollbackButton.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Show("Операция не выполнена", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
            ScanButton.IsEnabled = true;
            RepairButton.IsEnabled = ResultsList.SelectedItem is PolicyItem selected &&
                selected.Finding.State == RestrictionState.Restricted;
            RollbackButton.IsEnabled = _latestUndoable is not null;
        }
    }

    private void UpdateHistory()
    {
        var history = _engine.History();
        _latestUndoable = history.FirstOrDefault(r => r.Status is RecoveryStatus.Applied or
            RecoveryStatus.Prepared or RecoveryStatus.RecoveryRequired);
        HistoryText.Text = $"Резервных записей: {history.Count}. " +
            (_latestUndoable is null ? "Доступных для отката нет." :
                $"Последняя доступная для отката: {_latestUndoable.RuleId} ({_latestUndoable.Id:N}).");
        RollbackButton.IsEnabled = !_busy && _latestUndoable is not null;
    }

    private void Show(string title, string message, InfoBarSeverity severity)
    {
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

}

public sealed class PolicyItem(RestrictionFinding finding)
{
    public RestrictionFinding Finding { get; } = finding;
    public string Title => Finding.Rule.Title;
    public string StateText => Finding.State switch
    {
        RestrictionState.Restricted => "Ограничено · требуется оценка",
        RestrictionState.Allowed => "Явно разрешено",
        RestrictionState.NotConfigured => "Не настроено",
        _ => "Неизвестное значение · исправление недоступно"
    };
}
