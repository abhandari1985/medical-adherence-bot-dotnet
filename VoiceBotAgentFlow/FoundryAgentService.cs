using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;

public class FoundryService
{
    private readonly HttpClient _client;
    private readonly string? _apiKey;
    private readonly IConfiguration _config;
    private readonly SchedulingService _schedulingService;
    private readonly Patient _patient;

    public FoundryService(HttpClient client, IConfiguration config, SchedulingService schedulingService, Patient patient)
    {
        _client = client;
        _config = config;
        _apiKey = _config["Foundry:ApiKey"];
        _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        _schedulingService = schedulingService;
        _patient = patient;
    }

    public async Task<T?> CallAgentAsync<T>(string url, object payload)
    {
        // Check if in mock mode
        var mockModeValue = _config["Foundry:MockMode"] ?? "false";
        Console.WriteLine($"[DEBUG] MockMode config value: '{mockModeValue}'");
        var mockMode = bool.Parse(mockModeValue);
        Console.WriteLine($"[DEBUG] MockMode parsed as: {mockMode}");
        if (mockMode)
        {
            return CreateMockResponse<T>(url, payload);
        }

        var response = await _client.PostAsJsonAsync(url, payload);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>();
    }

    private T? CreateMockResponse<T>(string url, object payload)
    {
        Console.WriteLine($"[MOCK MODE] Calling agent: {url}");
        
        // Extract user message from payload for better routing
        string userMessage = "";
        try
        {
            var payloadJson = System.Text.Json.JsonSerializer.Serialize(payload);
            var payloadDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(payloadJson);
            if (payloadDict?.ContainsKey("input") == true)
            {
                var inputJson = System.Text.Json.JsonSerializer.Serialize(payloadDict["input"]);
                var inputDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(inputJson);
                userMessage = inputDict?.ContainsKey("user_message") == true ? inputDict["user_message"]?.ToString()?.ToLower() ?? "" : "";
            }
        }
        catch
        {
            userMessage = "";
        }
        
        Console.WriteLine($"[DEBUG] Extracted user message: '{userMessage}'");
        Console.WriteLine($"[DEBUG] Checking URL: '{url}'");
        
        // Return mock responses based on URL pattern and user intent
        // Match by agent ID patterns from config URLs
        var triageUrl = _config["Foundry:TriageUrl"] ?? "";
        var adherenceUrl = _config["Foundry:AdherenceUrl"] ?? "";
        var schedulingUrl = _config["Foundry:SchedulingUrl"] ?? "";
        
        if (url == triageUrl)
        {
            Console.WriteLine("[DEBUG] Processing Triage URL");
            string route = "general";
            if (userMessage.Contains("medication") || userMessage.Contains("dosage") || userMessage.Contains("pill"))
            {
                route = "ROUTE_TO_ADHERENCE";
            }
            else if (userMessage.Contains("appointment") || userMessage.Contains("schedule") || userMessage.Contains("visit"))
            {
                route = "ROUTE_TO_SCHEDULING";
            }
            else if (userMessage.Contains("pain") || userMessage.Contains("emergency") || userMessage.Contains("urgent"))
            {
                route = "ROUTE_TO_SAFETY";
            }
            
            Console.WriteLine($"[DEBUG] Triage routing to: {route}");
            var mockTriageResponse = new Dictionary<string, object>
            {
                ["output"] = route
            };
            Console.WriteLine($"[DEBUG] Created mock response with output: {mockTriageResponse["output"]}");
            return (T?)(object)mockTriageResponse;
        }
        else if (url == adherenceUrl)
        {
            var mockAdherenceResponse = new Dictionary<string, object>
            {
                ["output"] = "Based on your medication schedule, you should take your medication as prescribed by Dr. Patel. If you have concerns about dosage, please consult with your healthcare provider."
            };
            return (T?)(object)mockAdherenceResponse;
        }
        else if (url == schedulingUrl)
        {
            var mockSchedulingResponse = new Dictionary<string, object>
            {
                ["output"] = "create_appointment",
                ["appointment_date"] = "2025-08-15",
                ["appointment_time"] = "14:00"
            };
            return (T?)(object)mockSchedulingResponse;
        }

        return default(T);
    }

    public async Task<string?> CallTriageAgentAsync(string userMessage)
    {
        var payload = new
        {
            input = new
            {
                user_message = userMessage
            },
            session = new
            {
                patient_name = _patient.patientName,
                doctor_name = _patient.doctorName,
                medication_details = string.Join(", ", _patient.prescriptions?.Select(p => $"{p.medicationName} {p.dosage} {p.frequency}") ?? new List<string>())
            }
        };

        var result = await CallAgentAsync<Dictionary<string, object>>(
            _config["Foundry:TriageUrl"] ?? "",
            payload
        );

        Console.WriteLine($"[DEBUG] Triage result: {result}");
        var output = result?["output"]?.ToString();
        Console.WriteLine($"[DEBUG] Triage output: {output}");
        return output;
    }

    public async Task<string?> CallAdherenceAgentAsync(string userMessage)
    {
        var payload = new
        {
            input = new
            {
                user_message = userMessage
            },
            session = new
            {
                patient_name = _patient.patientName,
                doctor_name = _patient.doctorName,
                medication_details = string.Join(", ", _patient.prescriptions?.Select(p => $"{p.medicationName} {p.dosage} {p.frequency}") ?? new List<string>()),
                current_date = DateTime.UtcNow.ToString("yyyy-MM-dd")
            }
        };

        var result = await CallAgentAsync<Dictionary<string, object>>(
            _config["Foundry:AdherenceUrl"] ?? "",
            payload
        );

        return result? ["output"]?.ToString();
    }

    public async Task<string?> CallSchedulingAgentAsync(string userMessage)
    {
        var payload = new
        {
            input = new
            {
                user_message = userMessage
            },
            session = new
            {
                patient_name = _patient.patientName,
                doctor_name = _patient.doctorName,
                medication_details = string.Join(", ", _patient.prescriptions?.Select(p => $"{p.medicationName} {p.dosage} {p.frequency}") ?? new List<string>()),
                current_date = DateTime.UtcNow.ToString("yyyy-MM-dd"),
                discharge_date = _patient.dischargeDate,
                follow_up_window_days = _patient.followUpWindowDays
            }
        };

        var result = await CallAgentAsync<Dictionary<string, object>>(
            _config["Foundry:SchedulingUrl"] ?? "",
            payload
        );

        return result? ["output"]?.ToString();
    }

    public async Task<string> ProcessMessageAsync(string userText)
    {
        Console.WriteLine($"[DEBUG] Processing user message: {userText}");
        var route = await CallTriageAgentAsync(userText);
        Console.WriteLine($"[DEBUG] Triage agent returned: {route}");

        if (route?.Contains("ROUTE_TO_SAFETY") == true)
        {
            return "Thanks for letting me know. That could be important. If you're experiencing anything like chest pain, trouble breathing, or feeling very unwell, please call your doctor or 911 right away.";
        }

        if (route?.Contains("ROUTE_TO_ADHERENCE") == true)
        {
            Console.WriteLine("[DEBUG] Routing to adherence agent");
            var adherenceReply = await CallAdherenceAgentAsync(userText);
            return adherenceReply ?? "I couldn't process your adherence request.";
        }

        if (route?.Contains("ROUTE_TO_SCHEDULING") == true)
        {
            Console.WriteLine("[DEBUG] Routing to scheduling agent");
            // Example: Find available slots and create appointment
            var availableSlots = await _schedulingService.FindAvailabilityAsync(DateTime.UtcNow.Date);
            var slot = availableSlots.Count > 0 ? availableSlots[0] : null;
            if (slot != null)
            {
                var appointmentTime = DateTime.Parse(DateTime.UtcNow.Date.ToString("yyyy-MM-dd") + "T" + slot + ":00");
                var schedulingReply = await _schedulingService.CreateAppointmentAsync(appointmentTime, _patient.patientName ?? "Unknown Patient");
                return schedulingReply;
            }
            else
            {
                return "No available slots for scheduling.";
            }
        }

        return "Could you please clarify?";
    }
}
