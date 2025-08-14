using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;

public class TestFlows
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("=== Testing Voice Bot Agent Flows ===");
        
        // Setup configuration
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .Build();

        // Load patient data
        var patientJson = await System.IO.File.ReadAllTextAsync("../patients.json");
        var patients = System.Text.Json.JsonSerializer.Deserialize<List<Patient>>(patientJson);
        var patient = patients?[0] ?? new Patient();

        // Setup services
        var httpClient = new System.Net.Http.HttpClient();
        var schedulingService = new SchedulingService(configuration);
        var foundryService = new FoundryService(httpClient, configuration, schedulingService, patient);

        // Test scenarios
        await TestScenario("Scheduling Flow", "i need to schedule an appointment", foundryService);
        await TestScenario("Medication Flow", "can you help me with my medication dosage", foundryService);
        await TestScenario("Safety Flow", "i have chest pain", foundryService);
        
        Console.WriteLine("\n=== All Tests Complete ===");
    }

    private static async Task TestScenario(string scenarioName, string userInput, FoundryService foundryService)
    {
        Console.WriteLine($"\n--- {scenarioName} ---");
        Console.WriteLine($"User: {userInput}");
        
        try
        {
            var response = await foundryService.ProcessMessageAsync(userInput);
            Console.WriteLine($"Bot: {response}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }
}
