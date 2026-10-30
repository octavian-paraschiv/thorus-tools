using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThorusTools
{
    internal class ElevationResponse
    {
        [JsonPropertyName("elevation")]
        public double[] Elevations { get; set; }
    }

    internal class ErrorResponse
    {
        [JsonPropertyName("reason")]
        public string Reason { get; set; }
    }

    internal class CoordinateData
    {
        public double Lat { get; set; }
        public double Lon { get; set; }
        public double Elevation { get; set; }
    }

    internal class CoordinateFileData
    {
        public bool DownloadComplete { get; set; }
        public int LastSuccesfulChunkIndex { get; set; }
        public List<CoordinateData> Coordinates { get; set; }
    }

    public sealed class Double2DecimalsConverter : JsonConverter<double>
    {
        public override double Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            return reader.GetDouble();
        }

        public override void Write(
            Utf8JsonWriter writer,
            double value,
            JsonSerializerOptions options)
        {
            writer.WriteRawValue(
                value.ToString("F2", CultureInfo.InvariantCulture));
        }
    }

    internal class Program
    {
        private static void Main(string[] args)
        {
            const string baseUrl = "https://api.open-meteo.com/v1/";

            string basePath = Path.Combine(Environment.CurrentDirectory, "../../../../data");

            string tempFile = Path.Combine(basePath, "temp.thd");
            string elevFile = Path.Combine(basePath, "elevationMap.thd");
            string maskFile = Path.Combine(basePath, "landWaterMask.thd");

            const int GridWidth = 720;
            const int GridHeight = 360;

            JsonSerializerOptions opt = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };

            opt.Converters.Add(new Double2DecimalsConverter());

            StringBuilder sbElev = new StringBuilder();
            StringBuilder sbMask = new StringBuilder();

            var client = new HttpClient { BaseAddress = new Uri(baseUrl) };

            var coordinateData = new CoordinateFileData { Coordinates = [] };

            if (!File.Exists(tempFile))
            {
                Console.WriteLine($"The program was launched for the first time on this machine. Creating temporary download file at {tempFile}...");

                for (int row = 0; row < GridHeight; row++)
                {
                    double targetLat = 89.99 - row * 0.5;
                    if (targetLat < -89.99)
                        targetLat = 89.99;

                    for (int col = 0; col < GridWidth; col++)
                    {
                        double targetLon = -179.99 + col * 0.5;
                        if (targetLon > 179.99)
                            targetLon = 179.99;

                        coordinateData.Coordinates.Add(new() { Lat = targetLat, Lon = targetLon });
                    }
                }

                if (!Directory.Exists(basePath))
                    Directory.CreateDirectory(basePath);

                File.WriteAllText(tempFile, JsonSerializer.Serialize(coordinateData, opt));
            }
            else
            {
                var json = File.ReadAllText(tempFile);
                coordinateData = JsonSerializer.Deserialize<CoordinateFileData>(json);
            }

            if (!coordinateData.DownloadComplete)
            {
                var chunks = coordinateData.Coordinates.Chunk(100).ToArray();

                Console.WriteLine($"Download not complete, resuming from chunk #{coordinateData.LastSuccesfulChunkIndex} of {chunks.Length}...");

                for (int i = coordinateData.LastSuccesfulChunkIndex; i < chunks.Length; i++)
                {
                retry:
                    var latChunk = string.Join(',', chunks[i].Select(c => c.Lat.ToString("F2"))).Trim(',');
                    var lonChunk = string.Join(',', chunks[i].Select(c => c.Lon.ToString("F2"))).Trim(',');
                    string url = $"elevation?latitude={latChunk}&longitude={lonChunk}";

                    Console.WriteLine($"Downloading chunk #{i} of {chunks.Length}...");

                    var rsp = client.GetAsync(url).GetAwaiter().GetResult();

                    if (rsp.IsSuccessStatusCode)
                    {
                        var data = rsp.Content.ReadFromJsonAsync<ElevationResponse>().GetAwaiter().GetResult();
                        var elevations = data.Elevations;
                        for (int j = 0; j < elevations.Length; j++)
                            chunks[i][j].Elevation = elevations[j];

                        Console.WriteLine($" --> Chunk #{i} downloaded successfully!");

                        coordinateData.LastSuccesfulChunkIndex = i;

                        File.WriteAllText(tempFile, JsonSerializer.Serialize(coordinateData, opt));

                        Thread.Sleep(150);
                    }
                    else if (rsp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        var error = rsp.Content.ReadFromJsonAsync<ErrorResponse>().GetAwaiter().GetResult();

                        if (error.Reason.Contains("one minute"))
                        {
                            Console.WriteLine($" --> Too many requests! Waiting 1 minute before retrying chunk #{i}...");
                            Thread.Sleep(60 * 1000);
                        }
                        else if (error.Reason.Contains("next hour"))
                        {
                            Console.WriteLine($" --> Too many requests! Waiting 1 hour before retrying chunk #{i}...");
                            Thread.Sleep(60 * 60 * 1000);
                        }
                        else if (error.Reason.Contains("tomorrow"))
                        {
                            Console.WriteLine($" --> Too many requests! Waiting 24 hours before retrying chunk #{i}...");
                            Thread.Sleep(24 * 60 * 60 * 1000);
                        }

                        goto retry;
                    }
                }

                coordinateData.DownloadComplete = true;
                File.WriteAllText(tempFile, JsonSerializer.Serialize(coordinateData, opt));
            }

            if (coordinateData.DownloadComplete)
            {
                for (int row = 0; row < GridHeight; row++)
                {
                    double targetLat = 89.99 - row * 0.5;
                    if (targetLat < -89.99)
                        targetLat = 89.99;

                    StringBuilder sbcElev = new StringBuilder();
                    StringBuilder sbcMask = new StringBuilder();

                    for (int col = 0; col < GridWidth; col += 100)
                    {
                        double targetLon = -179.99 + col * 0.5;
                        if (targetLon > 179.99)
                            targetLon = 179.99;

                        var elev = coordinateData.Coordinates.FirstOrDefault(c => c.Lat == targetLat && c.Lon == targetLon).Elevation;

                        sbcElev.Append($"{elev:F2},");
                        sbcMask.Append($"{(elev > 0 ? "0" : "1")},");
                    }

                    sbElev.Append(sbcElev.ToString().Trim(','));
                    sbMask.Append(sbcMask.ToString().Trim(','));
                }

                File.WriteAllText(elevFile, sbElev.ToString());
                File.WriteAllText(maskFile, sbMask.ToString());
            }
        }
    }
}