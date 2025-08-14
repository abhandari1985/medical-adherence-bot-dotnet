using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

class Program
{
	static async Task Main(string[] args)
	{
		// Build configuration
		var config = new ConfigurationBuilder()
			.SetBasePath(AppContext.BaseDirectory)
			.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
			.Build();

		// Load patient data from patients.json
		var patientsJson = await File.ReadAllTextAsync("patients.json");
		var patients = JsonSerializer.Deserialize<List<Patient>>(patientsJson);
		var patient = patients != null && patients.Count > 0 ? patients[0] : null;
		if (patient == null)
		{
			Console.WriteLine("No patient data found.");
			return;
		}

		using var httpClient = new HttpClient();
		var schedulingService = new SchedulingService(config);
		var foundryService = new FoundryService(httpClient, config, schedulingService, patient);

		Console.WriteLine($"Bot loaded for patient: {patient.patientName} (Doctor: {patient.doctorName})");
		Console.WriteLine("Enter your message for the bot:");
		var userText = Console.ReadLine();

		var reply = await foundryService.ProcessMessageAsync(userText ?? "Hello, agent!");
		Console.WriteLine("Bot reply:");
		Console.WriteLine(reply);
	}
}
