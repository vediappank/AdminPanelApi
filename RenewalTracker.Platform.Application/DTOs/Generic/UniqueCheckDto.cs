namespace RenewalTracker.Platform.Application.DTOs.Generic;

/// <summary>
/// Answer of a live uniqueness check (e-mail / mobile) - GET .../contact-check and .../check-unique.
/// Message is ready to show under the field; the same check refuses the save with 400 { message }.
/// </summary>
public class UniqueCheckDto
{
    public bool Available { get; set; } = true;
    public string? Message { get; set; }

    public static UniqueCheckDto Ok() => new();
    public static UniqueCheckDto Taken(string message) => new() { Available = false, Message = message };
}
