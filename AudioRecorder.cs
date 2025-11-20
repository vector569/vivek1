using System;                              // Basic .NET types (DateTimeOffset, TimeSpan, etc.)
using System.IO;                           // File and directory handling (Path, Directory)
using NAudio.Wave;                         // NAudio library for audio capture and WAV writing

namespace SttRecorderApp                   // Namespace for all app classes
{
    public class AudioRecorder : IDisposable  // Manages microphone recording, segmentation, and cleanup
    {
        private WaveInEvent _waveIn;       // NAudio input device for recording from default microphone
        private WaveFileWriter _writer;    // Writes raw audio buffers into a WAV file on disk

        private string _folderPath;        // Folder where segment WAV files will be stored
        private string _currentFilePath;   // Full path to the currently active segment WAV file
        private bool _inSegment;           // Tracks whether we are currently inside an active speech segment
        private DateTimeOffset _segmentStartIst;     // IST timestamp when the current segment started
        private TimeSpan _currentSilence = TimeSpan.Zero; // Accumulated silence duration within the current segment

        // Simple VAD parameters (we can tune later)
        private const float SpeechThreshold = 0.005f; // Minimum RMS energy to consider the buffer as speech (voice activity detection)
        private readonly TimeSpan _maxSilenceInSegment = TimeSpan.FromMilliseconds(1000); // Silence duration (1s) that ends a segment

        private readonly TimeZoneInfo _istZone =      // Time zone object for India Standard Time
            TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");

        public bool IsListening { get; private set; } // Public flag exposed to UI to know if recording is currently active

        // Raised whenever one speech segment has been fully written to disk
        public event Action<AudioSegment> SegmentCompleted; // Event that notifies listeners when a segment finishes (with metadata)

        public void Start(string folderPath)          // Starts listening and writing segmented audio into the specified folder
        {
            if (IsListening)                          // If already listening, ignore repeated Start calls (idempotent)
            {
                return;                               // Early exit to avoid double-starting the microphone
            }

            _folderPath = folderPath;                 // Store the folder path for later segment file creation
            Directory.CreateDirectory(_folderPath);   // Ensure the directory exists (creates if missing, no-op if already there)

            _waveIn = new WaveInEvent                 // Create a new WaveInEvent instance for capturing audio
            {
                WaveFormat = new WaveFormat(16000, 1) // Configure audio format: 16 kHz sample rate, mono channel (good for STT)
            };

            _waveIn.DataAvailable += OnDataAvailable; // Subscribe to audio buffer callback to process incoming samples
            _waveIn.RecordingStopped += OnRecordingStopped; // Subscribe to recording stopped event to clean up properly

            _waveIn.StartRecording();                 // Begin capturing from the microphone asynchronously
            IsListening = true;                       // Update flag so UI and logic know recording is active
        }

        public void Stop()                            // Stops listening ( recording can be restarted later)
        {
            if (!IsListening)                         // If not currently listening, ignore Stop call
            {
                return;                               // Early exit to avoid calling StopRecording on a null or stopped device
            }

            _waveIn.StopRecording();                  // Ask NAudio to stop recording; triggers OnRecordingStopped eventually
        }

        private void OnDataAvailable(object sender, WaveInEventArgs e) // Callback whenever the mic has captured a chunk of audio
        {
            if (_waveIn == null || e.BytesRecorded <= 0) // If device is gone or buffer has no data, ignore this callback
                return;

            int bytesPerSample = 2;                   // 16-bit PCM: 2 bytes per audio sample
            int sampleCount = e.BytesRecorded / bytesPerSample; // Compute how many samples in this buffer
            if (sampleCount == 0) return;             // If no samples, nothing to process

            // Compute RMS energy for this buffer
            double sumSquares = 0;                    // Accumulator for squared sample values
            for (int i = 0; i < e.BytesRecorded; i += 2) // Iterate over buffer in 2-byte steps (each sample)
            {
                short sample = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8)); // Reconstruct 16-bit sample from two bytes (little-endian)
                float sample32 = sample / 32768f;     // Normalize sample to -1.0 .. +1.0 float range
                sumSquares += sample32 * sample32;    // Add squared sample amplitude to the sum
            }

            double rms = Math.Sqrt(sumSquares / sampleCount); // Root-mean-square amplitude of the current buffer
            bool isSpeech = rms > SpeechThreshold;    // Decide if this buffer is “speech” based on RMS vs threshold

            // Duration of this buffer
            double seconds = (double)sampleCount / _waveIn.WaveFormat.SampleRate; // Convert sample count to seconds
            TimeSpan bufferDuration = TimeSpan.FromSeconds(seconds); // Wrap seconds in a TimeSpan for easier accumulation

            if (!_inSegment)                          // If we are currently in a “silence” state (no active segment)
            {
                // Currently in silence; start a new segment when speech appears
                if (isSpeech)                         // If this buffer contains speech
                {
                    StartSegment();                   // Open a new segment and start writing to a fresh WAV file
                    WriteToSegment(e.Buffer, e.BytesRecorded); // Write this first speech buffer to the new segment
                    _currentSilence = TimeSpan.Zero;  // Reset silence counter since we are actively in speech
                }
                // else: ignore background silence      // If no speech, do nothing (no file is open yet)
            }
            else                                      // We are inside an active segment
            {
                // We are inside a segment: always write audio
                WriteToSegment(e.Buffer, e.BytesRecorded); // Append all audio (speech + silence) to the segment file

                if (isSpeech)                         // If this buffer has speech
                {
                    _currentSilence = TimeSpan.Zero;  // Reset silence counter so segment stays open
                }
                else                                  // Buffer is silence while in an active segment
                {
                    _currentSilence += bufferDuration; // Accumulate silence duration
                    if (_currentSilence >= _maxSilenceInSegment) // If silence exceeds allowed limit (e.g., 1 second)
                    {
                        EndSegment();                 // Close current segment and fire SegmentCompleted event
                        _currentSilence = TimeSpan.Zero; // Reset silence counter ready for a new segment
                    }
                }
            }
        }

        private void StartSegment()                   // Opens a new WAV file and marks the start of a speech segment
        {
            _segmentStartIst = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _istZone); // Capture segment start time in IST

            string fileName = _segmentStartIst.ToString("yyyy-MM-dd_HH-mm-ss_fff") + ".wav"; // Build timestamp-based filename
            _currentFilePath = Path.Combine(_folderPath, fileName); // Combine folder path and filename into full file path

            _writer = new WaveFileWriter(_currentFilePath, _waveIn.WaveFormat); // Initialize WAV writer with current audio format
            _inSegment = true;                            // Mark that we are now inside an active segment
        }

        private void WriteToSegment(byte[] buffer, int bytesRecorded) // Writes raw buffer bytes into current segment WAV file
        {
            if (_writer == null) return;              // If no writer is open (no segment), ignore call

            _writer.Write(buffer, 0, bytesRecorded);  // Append audio data to the WAV file
            _writer.Flush();                          // Flush to disk to ensure data is written promptly (safer if app crashes)
        }

        private void EndSegment()                     // Closes the current segment and notifies listeners
        {
            if (!_inSegment)                          // If no segment is active, nothing to end (guard against double calls)
                return;

            _writer?.Dispose();                       // Dispose writer to finalize WAV header and release file
            _writer = null;                           // Clear writer reference so we don’t accidentally reuse it

            var endIst = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _istZone); // Capture segment end timestamp in IST

            if (!string.IsNullOrEmpty(_currentFilePath)) // If we actually had a valid file path for this segment
            {
                var segment = new AudioSegment        // Create a new AudioSegment metadata object for this finished segment
                {
                    FilePath = _currentFilePath,      // Path to the WAV file on disk
                    StartTimeIst = _segmentStartIst,  // Recorded IST start time for the segment
                    EndTimeIst = endIst               // Recorded IST end time for the segment
                };

                SegmentCompleted?.Invoke(segment);    // Fire event so subscribers (MainWindow) can process the new segment
            }

            _currentFilePath = null;                  // Clear current file path since segment is finished
            _inSegment = false;                       // Mark that we are no longer inside a segment
        }

        private void OnRecordingStopped(object sender, StoppedEventArgs e) // Callback when recording stops (via StopRecording or error)
        {
            // If recording stopped while we were in a segment, close it cleanly
            if (_inSegment)                           // Check if a segment was active when recording stopped
            {
                EndSegment();                         // End segment and raise SegmentCompleted if needed
            }

            _waveIn.DataAvailable -= OnDataAvailable; // Unsubscribe from DataAvailable to avoid further callbacks
            _waveIn.RecordingStopped -= OnRecordingStopped; // Unsubscribe from RecordingStopped to avoid leaks

            _waveIn.Dispose();                        // Dispose WaveInEvent to release mic/audio resources
            _waveIn = null;                           // Clear reference to show device is no longer active
            IsListening = false;                      // Update public flag so UI knows recording has stopped
        }

        public void Dispose()                         // Implementation of IDisposable to clean up resources deterministically
        {
            if (IsListening)                          // If still listening when disposed
            {
                _waveIn.StopRecording();              // Ask NAudio to stop recording; triggers OnRecordingStopped
            }

            _writer?.Dispose();                       // Dispose current writer if any (ensures file is properly closed)
            _waveIn?.Dispose();                       // Dispose WaveInEvent if still around (extra safety)
        }
    }
}
