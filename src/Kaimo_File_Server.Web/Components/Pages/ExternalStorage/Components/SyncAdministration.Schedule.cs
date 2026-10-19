using System.Globalization;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Web.Components.Shared;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kaimo_File_Server.Web.Components.Pages.ExternalStorage.Components;

// Weekly schedule grid: hour slots painted by click, day/hour headers or a JS-driven drag rectangle.
public partial class SyncAdministration
{
    private enum SchedulePaintMode { Select, Deselect }
    private static readonly DayOfWeek[] ScheduleDays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    private string SchedulePaintModeClass => _schedulePaintMode == SchedulePaintMode.Select ? "cloud-schedule-scroll--paint-select" : "cloud-schedule-scroll--paint-deselect";
    private bool SchedulePaintValue => _schedulePaintMode == SchedulePaintMode.Select;
    private static string ScheduleDayName(DayOfWeek day) => CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day);
    private bool IsScheduleSlotActive(DayOfWeek day, int hour) => _model.ActiveScheduleSlots.Contains(CloudSyncSchedule.ToSlot(day, hour));
    private void SetScheduleSlot(DayOfWeek day, int hour, bool active)
    {
        int slot = CloudSyncSchedule.ToSlot(day, hour);
        if (active) _model.ActiveScheduleSlots.Add(slot); else _model.ActiveScheduleSlots.Remove(slot);
    }
    private void PaintScheduleSlot(DayOfWeek day, int hour) => SetScheduleSlot(day, hour, SchedulePaintValue);
    private void PaintScheduleDay(DayOfWeek day)
    {
        foreach (int hour in Enumerable.Range(0, CloudSyncSchedule.HoursPerDay)) SetScheduleSlot(day, hour, SchedulePaintValue);
    }
    private void PaintScheduleHour(int hour)
    {
        foreach (DayOfWeek day in ScheduleDays) SetScheduleSlot(day, hour, SchedulePaintValue);
    }
    private void ClearSchedule() => _model.ActiveScheduleSlots.Clear();
    // Enabling automatic sync defaults to every hour of every day; existing custom slots are kept.
    private void ScheduleToggled()
    {
        if (_model.ScheduleEnabled && _model.ActiveScheduleSlots.Count == 0)
            _model.ActiveScheduleSlots.UnionWith(Enumerable.Range(0, CloudSyncSchedule.SlotCount));
    }
    private string ScheduleDayActionLabel(DayOfWeek day) => string.Format(R("Web_CloudSync_Schedule_Day_Paint"), ScheduleDayName(day), R(_schedulePaintMode == SchedulePaintMode.Select ? "Web_CloudSync_Schedule_Paint_Select" : "Web_CloudSync_Schedule_Paint_Deselect"));
    private string ScheduleHourActionLabel(int hour) => string.Format(R("Web_CloudSync_Schedule_Hour_Paint"), hour.ToString("00"), R(_schedulePaintMode == SchedulePaintMode.Select ? "Web_CloudSync_Schedule_Paint_Select" : "Web_CloudSync_Schedule_Paint_Deselect"));
    private static string ScheduleSlotLabel(DayOfWeek day, int hour, bool active) => string.Format(R(active ? "Web_CloudSync_Schedule_Slot_On" : "Web_CloudSync_Schedule_Slot_Off"), ScheduleDayName(day), hour.ToString("00"), ((hour + 1) % CloudSyncSchedule.HoursPerDay).ToString("00"));
    [JSInvokable]
    public Task PaintScheduleRectangleFromJs(int startRow, int startHour, int endRow, int endHour)
    {
        if (!_model.ScheduleEnabled || startRow < 0 || endRow < 0 || startRow >= ScheduleDays.Length || endRow >= ScheduleDays.Length || startHour is < 0 or >= CloudSyncSchedule.HoursPerDay || endHour is < 0 or >= CloudSyncSchedule.HoursPerDay) return Task.CompletedTask;
        for (int row = Math.Min(startRow, endRow); row <= Math.Max(startRow, endRow); row++)
            for (int hour = Math.Min(startHour, endHour); hour <= Math.Max(startHour, endHour); hour++)
                SetScheduleSlot(ScheduleDays[row], hour, SchedulePaintValue);
        return InvokeAsync(StateHasChanged);
    }
}
