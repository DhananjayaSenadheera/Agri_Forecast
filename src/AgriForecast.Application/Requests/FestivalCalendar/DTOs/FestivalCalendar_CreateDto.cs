namespace AgriForecast.Application.Requests.FestivalCalendar.DTOs;

public class FestivalCalendar_CreateDto
{
    // Stable machine key, e.g. "AVURUDU". The ML feature layer string-matches it case-sensitively, so the
    // validator enforces the UPPERCASE [A-Z0-9_] convention.
    public string FestivalKey { get; set; }

    // Date-only on the wire and in storage: it is the ML as-of-join key and must not carry a time.
    public DateTime Date { get; set; }

    // Pre-festival demand window length in days. 0 is valid — it is the paired-day convention for a
    // multi-day festival — so the validator allows >= 0.
    public int LeadUpDays { get; set; }

    // Date not yet confirmed against the gazette. Passed straight through, never silently upgraded.
    public bool IsProvisional { get; set; }

    // Gazette citation. Required on every mutation: festivals are curated training-relevant data.
    public string? Source { get; set; }
}
