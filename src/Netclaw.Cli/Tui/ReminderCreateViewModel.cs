using System.Net.Http.Json;
using System.Text.Json;
using Netclaw.Cli.Daemon;
using R3;
using Termina.Reactive;

namespace Netclaw.Cli.Tui;

public enum ReminderCreateState
{
    Title,
    ScheduleType,
    Schedule,
    Instructions,
    NotifyInstructions,
    Confirm,
    Done
}

public sealed class ReminderCreateViewModel : ReactiveViewModel
{
    private readonly DaemonApi _api;

    public ReactiveProperty<ReminderCreateState> CurrentState { get; } = new(ReminderCreateState.Title);
    public ReactiveProperty<string> StatusMessage { get; } = new("Enter a short reminder title.");
    public ReactiveProperty<bool> IsSubmitting { get; } = new(false);
    public ReactiveProperty<int> StateVersion { get; } = new(0);

    public string Title { get; private set; } = string.Empty;
    public string ScheduleType { get; private set; } = "once";
    public string Schedule { get; private set; } = string.Empty;
    public string Instructions { get; private set; } = string.Empty;
    public string NotifyInstructions { get; private set; } = string.Empty;

    public ReminderCreateViewModel(DaemonApi api)
    {
        _api = api;
    }

    public void SetTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            StatusMessage.Value = "Title is required.";
            RequestRedraw();
            return;
        }

        Title = value.Trim();
        CurrentState.Value = ReminderCreateState.ScheduleType;
        StatusMessage.Value = "Select schedule type.";
        NotifyChanged();
    }

    public void SetScheduleType(string value)
    {
        ScheduleType = value.Trim().ToLowerInvariant();
        CurrentState.Value = ReminderCreateState.Schedule;
        StatusMessage.Value = "Enter schedule value (e.g. 30m, every 6h, 0 */6 * * *).";
        NotifyChanged();
    }

    public void SetSchedule(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            StatusMessage.Value = "Schedule value is required.";
            RequestRedraw();
            return;
        }

        Schedule = value.Trim();
        CurrentState.Value = ReminderCreateState.Instructions;
        StatusMessage.Value = "Enter reminder instructions.";
        NotifyChanged();
    }

    public void SetInstructions(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            StatusMessage.Value = "Instructions are required.";
            RequestRedraw();
            return;
        }

        Instructions = value.Trim();
        CurrentState.Value = ReminderCreateState.NotifyInstructions;
        StatusMessage.Value = "Enter delivery instructions.";
        NotifyChanged();
    }

    public void SetNotifyInstructions(string value)
    {
        NotifyInstructions = string.IsNullOrWhiteSpace(value)
            ? "Summarize the key result in one or two sentences."
            : value.Trim();

        CurrentState.Value = ReminderCreateState.Confirm;
        StatusMessage.Value = "Review and create reminder.";
        NotifyChanged();
    }

    public async Task SubmitAsync(CancellationToken ct = default)
    {
        if (IsSubmitting.Value)
            return;

        IsSubmitting.Value = true;
        StatusMessage.Value = "Validating schedule...";
        RequestRedraw();

        try
        {
            var payload = new
            {
                name = Title,
                prompt = Instructions,
                scheduleType = ScheduleType,
                schedule = Schedule,
                deliveryKind = "none",
                deliveryInstructions = NotifyInstructions
            };

            using var validate = await _api.ValidateReminderAsync(payload, ct);

            if (!validate.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(validate, ct);
                StatusMessage.Value = $"Validation failed: {error}";
                RequestRedraw();
                return;
            }

            StatusMessage.Value = "Creating reminder...";
            RequestRedraw();

            using var create = await _api.CreateReminderAsync(payload, ct);

            if (!create.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(create, ct);
                StatusMessage.Value = $"Create failed: {error}";
                RequestRedraw();
                return;
            }

            var response = await create.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var message = response.TryGetProperty("message", out var msg)
                ? msg.GetString() ?? "Reminder created."
                : "Reminder created.";

            CurrentState.Value = ReminderCreateState.Done;
            StatusMessage.Value = message;
            NotifyChanged();
        }
        catch (Exception ex)
        {
            StatusMessage.Value = $"Unable to reach daemon: {ex.Message}";
            RequestRedraw();
        }
        finally
        {
            IsSubmitting.Value = false;
        }
    }

    public void Reset()
    {
        Title = string.Empty;
        ScheduleType = "once";
        Schedule = string.Empty;
        Instructions = string.Empty;
        NotifyInstructions = string.Empty;
        CurrentState.Value = ReminderCreateState.Title;
        StatusMessage.Value = "Enter a short reminder title.";
        NotifyChanged();
    }

    public void GoBack()
    {
        switch (CurrentState.Value)
        {
            case ReminderCreateState.Title:
                Shutdown();
                return;
            case ReminderCreateState.ScheduleType:
                CurrentState.Value = ReminderCreateState.Title;
                break;
            case ReminderCreateState.Schedule:
                CurrentState.Value = ReminderCreateState.ScheduleType;
                break;
            case ReminderCreateState.Instructions:
                CurrentState.Value = ReminderCreateState.Schedule;
                break;
            case ReminderCreateState.NotifyInstructions:
                CurrentState.Value = ReminderCreateState.Instructions;
                break;
            case ReminderCreateState.Confirm:
                CurrentState.Value = ReminderCreateState.NotifyInstructions;
                break;
            case ReminderCreateState.Done:
                Shutdown();
                return;
        }

        NotifyChanged();
    }

    public void RequestQuit() => Shutdown();

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            if (json.TryGetProperty("error", out var err))
                return err.GetString() ?? response.ReasonPhrase ?? "request failed";
            return json.ToString();
        }
        catch
        {
            return response.ReasonPhrase ?? "request failed";
        }
    }

    private void NotifyChanged()
    {
        StateVersion.Value++;
        RequestRedraw();
    }

    public override void Dispose()
    {
        CurrentState.Dispose();
        StatusMessage.Dispose();
        IsSubmitting.Dispose();
        StateVersion.Dispose();
        base.Dispose();
    }
}
