# VoiceBotAgentFlow

A .NET 9 healthcare voice bot implementation that migrates Echo Bot logic to Azure AI Foundry with intelligent agent orchestration for Triage, Adherence, and Scheduling workflows.

## 🏥 Project Overview

This application demonstrates a complete healthcare voice bot migration from Bot Framework Echo Bot to Azure AI Foundry's agentic architecture. It implements a sophisticated triage → adherence → scheduling flow with Microsoft Graph integration for appointment management.

## 🏗️ Architecture

```text
Bot Framework Host → Foundry Orchestrator → [Triage Agent] → [Adherence Agent] → [Scheduling Agent] → Microsoft Graph API
                                              ↓                    ↓                    ↓
                                        ROUTE_TO_*        Medication Guidance    Calendar Management
```

### Agent Flow Logic

1. **Triage Agent**: Routes user messages to appropriate specialized agents
2. **Adherence Agent**: Handles medication dosage, side effects, and compliance guidance
3. **Scheduling Agent**: Manages appointment booking via Microsoft Graph integration
4. **Safety Override**: Immediate emergency response for critical health issues

## 🛠️ Implementation Status

### ✅ Completed Features

- **Project Setup**: .NET 9 console application with proper dependency injection
- **Agent Orchestration**: Complete FoundryService with triage → adherence → scheduling flow
- **Patient Context**: Patient model with prescription data loaded from JSON
- **Microsoft Graph Integration**: Full SchedulingService with calendar operations:

  - Find availability slots
  - Create appointments
  - List appointments
  - Cancel appointments
  - Reschedule appointments

- **Mock Mode**: Testing framework for development without live Azure AI Foundry endpoints
- **Safety Response**: Emergency health situation handling
- **Configuration Management**: appsettings.json for Azure AI Foundry and Graph API credentials

### 🔍 Progress Summary (highlights)

- Project scaffolded and end-to-end flows implemented and tested against live Foundry agents.
- Azure AI Foundry SDK integration (AIProjectClient + PersistentAgentsClient) using a Foundry-specific ClientSecretCredential.
- Per-patient/role thread reuse implemented so agents retain multi-turn context instead of creating a new thread per message.
- Session context is injected into threads so agent templates (e.g. `{{session.doctor_name}}`) resolve correctly.
- Assistant-only message aggregation with clearer separators (newlines) for readable bot replies.
- SchedulingService wired to Microsoft Graph for appointment operations; scheduling flows tested end-to-end.
- Interactive console host: `Program.cs` now runs a persistent prompt loop for multi-turn testing.
- Mock mode remains available for local testing without live endpoints.

### 🔔 Recent updates (2025-08-14)

- Implemented live Foundry SDK improvements: per-patient/role thread reuse and injected session now includes `discharge_date` and `follow_up_window_days` so templates like `{{session.discharge_date}}` render correctly.
- Fixed assistant reply aggregation so the bot returns only the latest assistant message (no longer concatenates previous replies).
- Added a scheduling-in-progress flag so follow-up user replies during scheduling are routed to the Scheduling agent until booking completes.
- Removed the extra console prompt string (previously printed "Enter your message for the bot (or type 'exit' to quit):") while preserving the interactive loop behavior.
- Reduced noisy debug output; terminal now shows essential [INFO], [WARN], and [ERROR] logs for clarity during testing.

Next steps (short-term):

- Implement structured JSON action parsing from agents to perform deterministic appointment creation (e.g., agents return `{ "action": "create_appointment", "date": "2025-08-17", "time": "11:00" }`).
- Persist the in-memory thread/session mapping to durable storage so conversation context survives application restarts.
- Migrate the host to Bot Framework for channel & voice support and add telemetry (Application Insights).

### 🔧 Production Deployment Ready

- **Agent URLs**: Configure your Azure AI Foundry agent endpoints
- **Authentication**: Microsoft Graph API and Azure AI Foundry credentials setup
- **Error Handling**: Comprehensive null-safety and exception management

## 🚀 Getting Started

### Prerequisites

- .NET 9 SDK
- Azure AI Foundry account with deployed agents
- Microsoft 365 account with Graph API permissions

### Configuration

1. **Update `appsettings.json`** with your credentials:

```json
{
  "Foundry": {
    "ApiKey": "your-foundry-api-key",
    "TriageUrl": "your-triage-agent-endpoint",
    "AdherenceUrl": "your-adherence-agent-endpoint",
    "SchedulingUrl": "your-scheduling-agent-endpoint",
    "MockMode": false
  },
  "Graph": {
    "ClientId": "your-azure-app-client-id",
    "TenantId": "your-azure-tenant-id",
    "ClientSecret": "your-client-secret",
    "UserId": "target-user-calendar-id"
  }
}
```

1. **Setup Patient Data** in `patients.json`:

```json
[
  {
    "patientName": "John Doe",
    "doctorName": "Dr. Smith",
    "prescriptions": [
      {
        "medicationName": "Lisinopril",
        "dosage": "10mg",
        "frequency": "Once daily"
      }
    ]
  }
]
```

### Running the Application

```pwsh
# Build the project
dotnet build

# Run with mock mode for testing
dotnet run --project VoiceBotAgentFlow

# Test different flows:
# - "i need to schedule an appointment" → Scheduling flow
# - "help with my medication dosage" → Adherence flow
# - "i have chest pain" → Emergency safety response
```

## 📁 Project Structure

```text
VoiceBotAgentFlow/
├── VoiceBotAgentFlow/
│   ├── Program.cs                 # Application entry point
│   ├── FoundryAgentService.cs     # Agent orchestration logic
│   ├── SchedulingService.cs       # Microsoft Graph calendar integration
│   ├── Patient.cs                 # Patient and prescription models
│   ├── appsettings.json           # Configuration settings
│   ├── VoiceBotAgentFlow.csproj   # Project dependencies
│   ├── bot.js                     # Original Echo Bot implementation (reference)
│   └── schedulingPlugin.js        # Original scheduling plugin (reference)
├── patients.json                  # Patient context data
├── test_flows.ps1                 # Test automation script
├── TestFlows.cs                   # Integration test scenarios
└── README.md                      # Project documentation
```

## 🔄 Agent Interaction Examples

### Scheduling Flow

```text
User: "i need to schedule an appointment"
Triage → ROUTE_TO_SCHEDULING → SchedulingService → "Appointment for Isha Miller created at 2025-08-14 09:00"
```

### Adherence Flow

```text
User: "can you help with my medication dosage"
Triage → ROUTE_TO_ADHERENCE → "Based on your medication schedule, take medication as prescribed by Dr. Patel"
```

### Safety Flow

```text
User: "i have chest pain"
Triage → ROUTE_TO_SAFETY → "That could be important. If experiencing chest pain, call your doctor or 911"
```

## 🧪 Testing

The application includes comprehensive mock mode for testing agent flows without live endpoints:

```pwsh
# Enable mock mode in appsettings.json
"MockMode": true

# Run test scenarios
.\test_flows.ps1
```

## 📦 Dependencies

- **Microsoft.Graph** (5.x): Microsoft Graph API integration
- **Azure.Identity** (1.x): Azure authentication
- **Microsoft.Extensions.Configuration**: Configuration management
- **System.Text.Json**: JSON serialization

## 🔐 Security & Compliance

- Null-safe implementation with proper error handling
- Secure credential management via configuration
- HIPAA-ready patient data handling
- Microsoft Graph API security compliance

## 🚀 Next Steps for Production

1. **Deploy Azure AI Foundry Agents**: Replace mock mode with live agent endpoints
2. **Bot Framework Integration**: Migrate from console to Bot Framework for voice/text channels
3. **Conversation State Management**: Implement session persistence
4. **Enhanced Error Logging**: Add Application Insights integration
5. **Unit Testing**: Comprehensive test coverage
6. **CI/CD Pipeline**: Azure DevOps deployment automation

## 📞 Support

This implementation successfully migrates Echo Bot functionality to Azure AI Foundry while maintaining the original triage → adherence → scheduling logic and Microsoft Graph calendar integration.

---
**Status**: ✅ **Core Implementation Complete** - Ready for Azure AI Foundry agent deployment
