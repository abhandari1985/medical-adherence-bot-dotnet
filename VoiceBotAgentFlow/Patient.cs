using System;
using System.Collections.Generic;

public class Prescription
{
    public string? medicationName { get; set; }
    public string? dosage { get; set; }
    public string? frequency { get; set; }
    public string? duration { get; set; }
}

public class Patient
{
    public string? DocumentID { get; set; }
    public string? dischargeDate { get; set; }
    public string? doctorName { get; set; }
    public int followUpWindowDays { get; set; }
    public string? patientName { get; set; }
    public string? phoneNumber { get; set; }
    public List<Prescription>? prescriptions { get; set; }
}
