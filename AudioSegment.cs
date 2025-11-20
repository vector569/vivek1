using System;                                      // Brings in DateTimeOffset, TimeSpan, and other base types

namespace SttRecorderApp                           // Namespace shared with the rest of the app
{
    public class AudioSegment                      // Lightweight model representing one recorded speech segment
    {
        public string FilePath { get; init; }      // Full path to the WAV file on disk for this segment (init-only for immutability)
        public DateTimeOffset StartTimeIst { get; init; } // Segment start timestamp in IST (when speech began)
        public DateTimeOffset EndTimeIst { get; init; }   // Segment end timestamp in IST (when speech ended)

        public TimeSpan Duration => EndTimeIst - StartTimeIst; // Computed property for segment length (end − start)

        public override string ToString()          // Override to provide a nice human-readable representation
        {
            // This is handy if we ever bind directly to the object (e.g., in a ListBox)
            return $"{StartTimeIst:HH:mm:ss} ({Duration.TotalSeconds:F1}s) - {System.IO.Path.GetFileName(FilePath)}";
            // Shows start time (hh:mm:ss), duration in seconds, and just the file name (no full path)
        }
    }
}
