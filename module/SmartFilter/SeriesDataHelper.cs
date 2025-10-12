using Newtonsoft.Json.Linq;

namespace SmartFilter
{
    internal static class SeriesDataHelper
    {
        public static (JArray Data, JArray Voice, string MaxQuality) Unpack(JToken payload)
        {
            if (payload is JObject obj)
            {
                var data = obj["data"] as JArray ?? new JArray();
                var voice = obj["voice"] as JArray;
                if (voice != null && voice.Count == 0)
                    voice = null;

                string quality = obj.Value<string>("maxquality") ?? obj.Value<string>("quality");
                return (data, voice, quality);
            }

            return (payload as JArray ?? new JArray(), null, null);
        }
    }
}
