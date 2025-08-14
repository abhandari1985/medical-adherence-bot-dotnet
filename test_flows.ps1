# Test script to demonstrate different agent flows

Write-Host "=== Testing Voice Bot Agent Flows ===" -ForegroundColor Green

# Test 1: Scheduling Flow
Write-Host "`n1. Testing Scheduling Flow..." -ForegroundColor Yellow
echo "i need to schedule an appointment" | dotnet run --project VoiceBotAgentFlow

# Test 2: Medication/Adherence Flow  
Write-Host "`n2. Testing Medication/Adherence Flow..." -ForegroundColor Yellow
echo "can you help me with my medication dosage" | dotnet run --project VoiceBotAgentFlow

# Test 3: Safety/Emergency Flow
Write-Host "`n3. Testing Safety/Emergency Flow..." -ForegroundColor Yellow
echo "i have chest pain" | dotnet run --project VoiceBotAgentFlow

Write-Host "`n=== All Tests Complete ===" -ForegroundColor Green
