using System.Text.Json.Serialization;

namespace WindowsNotifyMcp;

// Native AOT cannot discover missing JSON contracts through reflection at runtime.
// Keep tool input and result types here and pass these options to tool registration.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NotificationButton[]))]
[JsonSerializable(typeof(NotificationSubmission))]
[JsonSerializable(typeof(NotificationResult))]
public partial class NotificationJsonContext : JsonSerializerContext;
