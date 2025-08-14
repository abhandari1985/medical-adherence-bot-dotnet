using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.Calendar.CalendarView;
using Microsoft.Graph.Users.Item.Events;
using Microsoft.Kiota.Abstractions;
using Microsoft.Extensions.Configuration;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;

public class SchedulingService
{
    private readonly GraphServiceClient _graphClient;
    private readonly string? _calendarUserId;

    public SchedulingService(IConfiguration config)
    {
    var tenantId = config["Graph:TenantId"];
    var clientId = config["Graph:ClientId"];
    var clientSecret = config["Graph:ClientSecret"];
    _calendarUserId = config["Graph:UserId"];

    var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
    _graphClient = new GraphServiceClient(credential);
    }

    // Example: Find available slots for a given date
    public async Task<List<string>> FindAvailabilityAsync(DateTime date)
    {
        var businessHours = new List<string> { "09:00", "11:00", "14:00" };
        var availableSlots = new List<string>(businessHours);

        var startDateTime = date.Date.ToString("yyyy-MM-ddTHH:mm:ss");
        var endDateTime = date.Date.AddDays(1).AddSeconds(-1).ToString("yyyy-MM-ddTHH:mm:ss");

        if (string.IsNullOrEmpty(_calendarUserId)) return availableSlots;

        var events = await _graphClient.Users[_calendarUserId].Calendar.CalendarView
            .GetAsync(q =>
            {
                q.QueryParameters.StartDateTime = startDateTime;
                q.QueryParameters.EndDateTime = endDateTime;
            });

        if (events?.Value != null)
        {
            foreach (var evt in events.Value)
            {
                var evtStart = evt?.Start?.DateTime;
                if (!string.IsNullOrEmpty(evtStart))
                {
                    var evtTime = DateTime.Parse(evtStart).ToString("HH:mm");
                    availableSlots.Remove(evtTime);
                }
            }
        }
        return availableSlots;
    }

    // Example: Create a new appointment
    public async Task<string> CreateAppointmentAsync(DateTime appointmentDateTime, string patientName)
    {
        var endDateTime = appointmentDateTime.AddHours(1);
        var userTimeZone = "Eastern Standard Time";
        var @event = new Event
        {
            Subject = $"Appointment for {patientName}",
            Start = new DateTimeTimeZone
            {
                DateTime = appointmentDateTime.ToString("yyyy-MM-ddTHH:mm:ss"),
                TimeZone = userTimeZone
            },
            End = new DateTimeTimeZone
            {
                DateTime = endDateTime.ToString("yyyy-MM-ddTHH:mm:ss"),
                TimeZone = userTimeZone
            },
            Attendees = new List<Attendee>()
        };

        var result = await _graphClient.Users[_calendarUserId].Events.PostAsync(@event);
        return $"Appointment for {patientName} created at {appointmentDateTime:yyyy-MM-dd HH:mm}. Event ID: {result?.Id}";
    }

    // Example: List appointments for a specific date
    public async Task<List<string>> ListAppointmentsAsync(DateTime date)
    {
        var startDateTime = date.Date.ToString("yyyy-MM-ddTHH:mm:ss");
        var endDateTime = date.Date.AddDays(1).AddSeconds(-1).ToString("yyyy-MM-ddTHH:mm:ss");

        if (string.IsNullOrEmpty(_calendarUserId)) return new List<string>();

        var events = await _graphClient.Users[_calendarUserId].Calendar.CalendarView
            .GetAsync(q =>
            {
                q.QueryParameters.StartDateTime = startDateTime;
                q.QueryParameters.EndDateTime = endDateTime;
            });

        var appointments = new List<string>();
        if (events?.Value != null)
        {
            foreach (var evt in events.Value)
            {
                var subject = evt?.Subject ?? "(No Subject)";
                var evtStart = evt?.Start?.DateTime;
                var time = !string.IsNullOrEmpty(evtStart) ? evtStart : "(No Time)";
                appointments.Add($"{subject} at {time}");
            }
        }
        return appointments;
    }

    // Example: Cancel an appointment
    public async Task<string> CancelAppointmentAsync(DateTime date, string time)
    {
        var startDateTime = date.Date.ToString("yyyy-MM-ddTHH:mm:ss");
        var endDateTime = date.Date.AddDays(1).AddSeconds(-1).ToString("yyyy-MM-ddTHH:mm:ss");

        if (string.IsNullOrEmpty(_calendarUserId)) return $"No appointment found on {date:yyyy-MM-dd} at {time}.";

        var events = await _graphClient.Users[_calendarUserId].Calendar.CalendarView
            .GetAsync(q =>
            {
                q.QueryParameters.StartDateTime = startDateTime;
                q.QueryParameters.EndDateTime = endDateTime;
            });

        if (events?.Value != null)
        {
            foreach (var evt in events.Value)
            {
                var evtStart = evt?.Start?.DateTime;
                if (!string.IsNullOrEmpty(evtStart))
                {
                    var evtTime = DateTime.Parse(evtStart).ToString("HH:mm");
                    if (evtTime == time)
                    {
                        if (!string.IsNullOrEmpty(evt?.Id))
                        {
                            await _graphClient.Users[_calendarUserId].Events[evt.Id].DeleteAsync();
                            return $"Appointment on {date:yyyy-MM-dd} at {time} cancelled.";
                        }
                    }
                }
            }
        }
        return $"No appointment found on {date:yyyy-MM-dd} at {time}.";
    }

    // Example: Reschedule an appointment
    public async Task<string> RescheduleAppointmentAsync(DateTime originalDate, string originalTime, DateTime newDateTime, string patientName)
    {
        var cancelResult = await CancelAppointmentAsync(originalDate, originalTime);
        if (!cancelResult.Contains("cancelled"))
        {
            return $"Failed to reschedule: {cancelResult}";
        }
        var createResult = await CreateAppointmentAsync(newDateTime, patientName);
        return $"Rescheduled: {createResult}";
    }
}
