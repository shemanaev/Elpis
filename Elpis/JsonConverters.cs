using Newtonsoft.Json;
using System;
using Util;

namespace Elpis
{
    internal class EncryptedStringConverter : JsonConverter<string>
    {
        private const string _cryptCheck = "*_a3fc756b42_*";
        private readonly string _cryptPass = SystemInfo.GetUniqueHash();

        public override void WriteJson(JsonWriter writer, string value, JsonSerializer serializer)
        {
            writer.WriteValue(StringCrypt.EncryptString(_cryptCheck + value, _cryptPass));
        }

        public override string ReadJson(JsonReader reader, Type objectType, string existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            var result = string.Empty;

            try
            {
                var val = StringCrypt.DecryptString((string)reader.Value, _cryptPass);

                if (!string.IsNullOrWhiteSpace(val) && val.StartsWith(_cryptCheck))
                    result = val.Replace(_cryptCheck, string.Empty);
            }
            catch (Exception e)
            {
                Log.O("Error decrypting string: {0}", e);
            }

            return result;
        }
    }

    internal class VersionConverter : JsonConverter<Version>
    {
        public override void WriteJson(JsonWriter writer, Version value, JsonSerializer serializer)
        {
            writer.WriteValue(value.ToString());
        }

        public override Version ReadJson(JsonReader reader, Type objectType, Version existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            return new Version((string)reader.Value);
        }
    }

    internal class HotkeyConfigConverter : JsonConverter<HotkeyConfig>
    {
        public override void WriteJson(JsonWriter writer, HotkeyConfig value, JsonSerializer serializer)
        {
            writer.WriteValue(value.ToString());
        }

        public override HotkeyConfig ReadJson(JsonReader reader, Type objectType, HotkeyConfig existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            return new HotkeyConfig((string)reader.Value, HotkeyConfig.Default);
        }
    }
}
