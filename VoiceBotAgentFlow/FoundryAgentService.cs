using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Azure;
using Azure.Identity;
using Azure.AI.Projects;
using Azure.AI.Agents.Persistent;

public class FoundryService
{
    private readonly HttpClient _client;
    private readonly string? _apiKey;
    private readonly IConfiguration _config;
    private readonly SchedulingService _schedulingService;
    private readonly Patient _patient;
    private readonly AIProjectClient? _projectClient;
    private readonly PersistentAgentsClient? _agentsClient;
    private readonly ClientSecretCredential? _credential;

    private List<(string role, string content)> _conversationHistory = new List<(string, string)>();
    private bool _adherenceCompleted = false;
    // track whether the adherence agent requested scheduling
    private bool _adherenceRequestedScheduling = false;
    private bool _schedulingCompleted = false;
    // track whether a scheduling flow is currently in progress (so follow-up user messages go to the scheduling agent)
    private bool _schedulingRequested = false;

    // Keep a mapping of patient+agent -> threadId so we reuse threads for multi-turn
    private readonly Dictionary<string, string> _threadByAgent = new Dictionary<string, string>();

    private string GetAgentRoleFromUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return "unknown";
        var triageUrl = _config["Foundry:TriageUrl"] ?? "";
        var adherenceUrl = _config["Foundry:AdherenceUrl"] ?? "";
        var schedulingUrl = _config["Foundry:SchedulingUrl"] ?? "";

        if (url.Equals(triageUrl, StringComparison.OrdinalIgnoreCase)) return "triage";
        if (url.Equals(adherenceUrl, StringComparison.OrdinalIgnoreCase)) return "adherence";
        if (url.Equals(schedulingUrl, StringComparison.OrdinalIgnoreCase)) return "scheduling";
        // fallback: try extract agent id
        var aid = ExtractAgentIdFromUrl(url);
        return string.IsNullOrEmpty(aid) ? "unknown" : aid;
    }

    public FoundryService(HttpClient client, IConfiguration config, SchedulingService schedulingService, Patient patient)
    {
        _client = client;
        _config = config;
        _apiKey = _config["Foundry:ApiKey"];

        // Remove attempt to set Content-Type or other content headers on HttpClient default headers
        _client.DefaultRequestHeaders.Clear();

        _schedulingService = schedulingService;
        _patient = patient;

        // Configure Azure AD service principal (headless/server) for Foundry SDK
        // Use Foundry-specific service principal credentials so Graph creds remain dedicated to Microsoft Graph usage
        var tenantId = _config["Foundry:TenantId"] ?? _config["Graph:TenantId"];
        var clientId = _config["Foundry:ClientId"] ?? _config["Graph:ClientId"];
        var clientSecret = _config["Foundry:ClientSecret"] ?? _config["Graph:ClientSecret"];

        if (!string.IsNullOrEmpty(tenantId) && !string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret))
        {
            Console.WriteLine($"[INFO] Initializing Foundry ClientSecretCredential for tenant: {tenantId}");
            _credential = new ClientSecretCredential(tenantId, clientId, clientSecret);

            // Project endpoint - prefer config value, fallback to provided project endpoint
            var projectEndpoint = _config["Foundry:ProjectEndpoint"] ?? "https://adherence-agent-anand-foundry.services.ai.azure.com/api/projects/adherence-agentic-flow";

            try
            {
                _projectClient = new AIProjectClient(new Uri(projectEndpoint), _credential);
                _agentsClient = _projectClient.GetPersistentAgentsClient();
                Console.WriteLine($"[INFO] Initialized AIProjectClient for project endpoint: {projectEndpoint}");

                // Debug: verify configured agent IDs exist by extracting from configured URLs
                try
                {
                    Console.WriteLine("[INFO] Verifying configured agents from Foundry URLs:");
                    var urls = new[]
                    {
                        _config["Foundry:TriageUrl"],
                        _config["Foundry:AdherenceUrl"],
                        _config["Foundry:SchedulingUrl"]
                    };

                    foreach (var u in urls)
                    {
                        if (string.IsNullOrEmpty(u)) continue;
                        var aid = ExtractAgentIdFromUrl(u);
                        if (string.IsNullOrEmpty(aid))
                        {
                            Console.WriteLine($"  - Could not extract agent id from URL: {u}");
                            continue;
                        }

                        try
                        {
                            var aResp = _agentsClient.Administration.GetAgent(aid);
                            var aVal = aResp.Value;
                            var agentName = aVal?.Name ?? "(no-name)";
                            var agentModel = aVal?.Model ?? "(no-model)";
                            Console.WriteLine($"  - Name: {agentName}, Id: {aVal?.Id}, Model: {agentModel}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  - Agent '{aid}' not found or retrieval failed: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Failed to verify agents: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Could not initialize AIProjectClient: {ex.Message}");
                _projectClient = null;
                _agentsClient = null;
            }
        }
        else
        {
            Console.WriteLine("[WARN] Missing Graph service principal settings (TenantId/ClientId/ClientSecret). SDK mode disabled.");
            _projectClient = null;
            _agentsClient = null;
            _credential = null;
        }
    }

    public async Task<T?> CallAgentAsync<T>(string url, object payload)
    {
        // Check if in mock mode
        var mockMode = bool.Parse(_config["Foundry:MockMode"] ?? "false");
        if (mockMode)
        {
            return CreateMockResponse<T>(url, payload);
        }

        // If SDK clients are available, prefer SDK flow
        if (_agentsClient != null)
        {
            try
            {
                Console.WriteLine($"[DEBUG] Calling Azure AI Foundry agent via SDK for URL: {url}");
                var userMessage = ExtractUserMessage(payload);
                Console.WriteLine($"[DEBUG] User message: {userMessage}");

                // Extract agent id from configured agent URL (pattern: asst_<id>)
                var agentId = ExtractAgentIdFromUrl(url);
                if (string.IsNullOrEmpty(agentId))
                {
                    Console.WriteLine("[ERROR] Could not extract agent id from URL; falling back to HTTP POST");
                    return await CallAgentViaHttpFallback<T>(url, payload);
                }

                // Determine role key for logging
                var roleKey = GetAgentRoleFromUrl(url);
                Console.WriteLine($"[INFO] Calling agent {agentId} (role: {roleKey})");

                // Get agent metadata (Administration.GetAgent)
                var agentResp = _agentsClient.Administration.GetAgent(agentId);
                var agent = agentResp.Value;

                // Determine or create a thread for this patient+agent so the agent retains multi-turn context
                // roleKey already computed
                var threadKey = ($"{_patient.patientName}:{roleKey}").ToLowerInvariant();
                string? threadId = null;
                var isNewThread = false;
                if (!_threadByAgent.TryGetValue(threadKey, out var existingThreadId) || string.IsNullOrEmpty(existingThreadId))
                {
                    var createdThreadResponse = _agentsClient.Threads.CreateThread();
                    var createdThread = createdThreadResponse.Value;
                    threadId = createdThread.Id;
                    _threadByAgent[threadKey] = threadId;
                    isNewThread = true;
                    Console.WriteLine($"[INFO] Created thread: {threadId}");
                }
                else
                {
                    threadId = existingThreadId;
                }

                // Inject session/context as a system message so the agent can resolve placeholders like {{session.doctor_name}} only on new thread
                var sessionObj = new
                {
                    doctor_name = _patient.doctorName,
                    patient_name = _patient.patientName,
                    medication_details = string.Join(", ", _patient.prescriptions?.Select(p => $"{p.medicationName} {p.dosage} {p.frequency}") ?? new List<string>()),
                    current_date = DateTime.UtcNow.ToString("yyyy-MM-dd"),
                    discharge_date = _patient.dischargeDate ?? "",
                    follow_up_window_days = _patient.followUpWindowDays
                };
                var sessionJson = JsonSerializer.Serialize(sessionObj);
                try
                {
                    if (isNewThread)
                    {
                        // Post as a user message with a SYSTEM_CONTEXT prefix so agent prompts can detect it
                        _agentsClient.Messages.CreateMessage(threadId, MessageRole.User, "[SYSTEM_CONTEXT]" + sessionJson);
                        Console.WriteLine("[INFO] Injected session context into thread");
                    }
                }
                catch (Exception)
                {
                    Console.WriteLine("[WARN] Failed to inject session context");
                }

                // Post user message to thread
                PersistentThreadMessage messageResponse = _agentsClient.Messages.CreateMessage(
                    threadId,
                    MessageRole.User,
                    userMessage
                );

                // Start a run for the agent against the thread
                ThreadRun run = _agentsClient.Runs.CreateRun(
                    threadId,
                    agent.Id
                );

                // Poll until the run reaches a terminal status
                do
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500));
                    run = _agentsClient.Runs.GetRun(threadId, run.Id);
                }
                while (run.Status == RunStatus.Queued || run.Status == RunStatus.InProgress);

                if (run.Status != RunStatus.Completed)
                {
                    Console.WriteLine($"[ERROR] Agent run failed or canceled: {run.LastError?.Message}");
                    var errorResult = new Dictionary<string, object>
                    {
                        ["output"] = $"Error: Agent run failed ({run.Status})"
                    };
                    return (T?)(object)errorResult;
                }

                // Retrieve messages and aggregate assistant text
                Pageable<PersistentThreadMessage> messages = _agentsClient.Messages.GetMessages(threadId, order: ListSortOrder.Ascending);
                // Only include the latest assistant message generated by this run to avoid concatenating previous replies
                PersistentThreadMessage? latestAssistantMessage = null;
                foreach (PersistentThreadMessage threadMessage in messages)
                {
                    var isAssistant = threadMessage.Role.ToString().Equals("assistant", StringComparison.OrdinalIgnoreCase);
                    if (!isAssistant) continue;
                    latestAssistantMessage = threadMessage; // keeps updating so ends with last assistant message
                }

                var assistantText = new System.Text.StringBuilder();
                if (latestAssistantMessage != null)
                {
                    foreach (MessageContent contentItem in latestAssistantMessage.ContentItems)
                    {
                        if (contentItem is MessageTextContent textItem)
                        {
                            var raw = textItem.Text?.Trim() ?? "";
                            if (raw.StartsWith("[SYSTEM_CONTEXT]", StringComparison.OrdinalIgnoreCase)) continue;
                            var txt = raw;
                            if (txt.StartsWith("{") && txt.EndsWith("}"))
                            {
                                try
                                {
                                    using var dj = System.Text.Json.JsonDocument.Parse(txt);
                                    continue; // skip pure JSON assistant content
                                }
                                catch { /* not valid JSON, keep text */ }
                            }
                            if (!string.IsNullOrEmpty(txt))
                            {
                                assistantText.Append(txt);
                            }
                        }
                    }
                }

                var result = new Dictionary<string, object>
                {
                    ["output"] = assistantText.ToString()
                };

                return (T?)(object)result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Exception calling agent via SDK: {ex.Message}");
                Console.WriteLine($"[INFO] Falling back to HTTP POST call for diagnostics");
                return await CallAgentViaHttpFallback<T>(url, payload);
            }
        }

        // Fallback to previous HTTP approach if SDK not available
        return await CallAgentViaHttpFallback<T>(url, payload);
    }

    private async Task<T?> CallAgentViaHttpFallback<T>(string url, object payload)
    {
        try
        {
            Console.WriteLine($"[DEBUG] Calling Azure AI Foundry agent via HTTP fallback: {url}");
            var userMessage = ExtractUserMessage(payload);
            var chatPayload = new
            {
                query = userMessage,
                stream = false
            };

            Console.WriteLine($"[DEBUG] Sending payload: {JsonSerializer.Serialize(chatPayload)}");
            var response = await _client.PostAsJsonAsync(url, chatPayload);

            Console.WriteLine($"[DEBUG] Response status: {response.StatusCode}");
            var content = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"[DEBUG] Response content: {content}");

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[ERROR] Azure AI Foundry returned error: {response.StatusCode}");
                var errorResult = new Dictionary<string, object>
                {
                    ["output"] = $"Error: Agent temporarily unavailable ({response.StatusCode})"
                };
                return (T?)(object)errorResult;
            }

            var result = new Dictionary<string, object>
            {
                ["output"] = content
            };
            return (T?)(object)result;
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"[ERROR] Failed to call Azure AI Foundry agent (HTTP fallback): {ex.Message}");
            var errorResult = new Dictionary<string, object>
            {
                ["output"] = "Error: Service temporarily unavailable"
            };
            return (T?)(object)errorResult;
        }
    }

    private string ExtractAgentIdFromUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;
        try
        {
            // Look for pattern asst_<id>
            var match = System.Text.RegularExpressions.Regex.Match(url, "asst_[A-Za-z0-9_-]+");
            if (match.Success)
            {
                return match.Value;
            }
        }
        catch { }
        return string.Empty;
    }

    private string ExtractUserMessage(object payload)
    {
        try
        {
            var payloadJson = JsonSerializer.Serialize(payload);
            var payloadDict = JsonSerializer.Deserialize<Dictionary<string, object>>(payloadJson);
            if (payloadDict?.ContainsKey("input") == true)
            {
                var inputJson = JsonSerializer.Serialize(payloadDict["input"]);
                var inputDict = JsonSerializer.Deserialize<Dictionary<string, object>>(inputJson);
                return inputDict?.ContainsKey("user_message") == true ? inputDict["user_message"]?.ToString() ?? "" : "";
            }
        }
        catch
        {
            // Fallback
        }
        return "";
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

        return result?["output"]?.ToString();
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
        // Special local trigger to generate a welcome message without calling agents
        if (userText == "__START_CALL__")
        {
            var patientName = _patient.patientName ?? "Patient";
            var doctorName = _patient.doctorName ?? "your doctor";
            var meds = _patient.prescriptions != null && _patient.prescriptions.Count > 0
                ? string.Join(", ", _patient.prescriptions.Select(p => p.medicationName))
                : "your medications";

            var welcome = $"Hello {patientName}! This is Ava calling on behalf of Dr. {doctorName}. I'm here to check on your recovery and your prescribed {meds}. Have you picked up your medication?";
            // track assistant message
            _conversationHistory.Add(("assistant", welcome));
            return welcome;
        }

        // Track user message
        _conversationHistory.Add(("user", userText));

        // Ask triage to route
        var route = await CallTriageAgentAsync(userText);

        if (route?.Contains("ROUTE_TO_SAFETY") == true)
        {
            var safety = "Thanks for letting me know. That could be important. If you're experiencing anything like chest pain, trouble breathing, or feeling very unwell, please call your doctor or 911 right away.";
            _conversationHistory.Add(("assistant", safety));
            return safety;
        }

        // Helper to detect scheduling signal in agent replies
        bool IsSchedulingSignal(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            var lower = text.ToLowerInvariant();
            return lower.Contains("create_appointment") || lower.Contains("schedule") || lower.Contains("appointment") || lower.Contains("route_to_scheduling");
        }

        // Only call adherence when triage explicitly routes to adherence
        if (route?.Contains("ROUTE_TO_ADHERENCE") == true)
        {
            var adherenceReply = await CallAdherenceAgentAsync(userText);
            // add assistant reply if not duplicate
            if (!string.IsNullOrEmpty(adherenceReply) && !_conversationHistory.Any(h => h.role == "assistant" && h.content == adherenceReply))
            {
                _conversationHistory.Add(("assistant", adherenceReply));
            }

            // If adherence agent indicates scheduling, move to scheduling
            if (IsSchedulingSignal(adherenceReply ?? ""))
            {
                _adherenceCompleted = true;
                _adherenceRequestedScheduling = true;
                // Start scheduling flow only after adherence signaled scheduling
                var schedulingReply = await CallSchedulingAgentAsync(userText);
                if (!string.IsNullOrEmpty(schedulingReply) && !_conversationHistory.Any(h => h.role == "assistant" && h.content == schedulingReply))
                {
                    _conversationHistory.Add(("assistant", schedulingReply));
                }
                return schedulingReply ?? "I couldn't schedule at this time.";
            }

            // Otherwise, return the agent's reply and wait for user response to continue the adherence flow
            return adherenceReply ?? "I couldn't process your adherence request.";
        }

        // Call scheduling only if triage routes there explicitly or adherence previously requested scheduling
        if (route?.Contains("ROUTE_TO_SCHEDULING") == true || _adherenceRequestedScheduling || _schedulingRequested)
        {
            // Mark that scheduling flow is active so subsequent user replies are routed to scheduling
            _schedulingRequested = true;
            var schedulingReply = await CallSchedulingAgentAsync(userText);
            if (!string.IsNullOrEmpty(schedulingReply) && !_conversationHistory.Any(h => h.role == "assistant" && h.content == schedulingReply))
            {
                _conversationHistory.Add(("assistant", schedulingReply));
            }

            // Simple heuristic: if scheduling reply contains confirmation of appointment, mark completed and clear request flag
            if (!string.IsNullOrEmpty(schedulingReply) && schedulingReply.ToLowerInvariant().Contains("appointment"))
            {
                _schedulingCompleted = true;
                // clear scheduling state when an appointment confirmation is detected
                _adherenceRequestedScheduling = false;
                _schedulingRequested = false;
            }

            return schedulingReply ?? "I couldn't process the scheduling request.";
        }

        return "Could you please clarify?";
    }
}
