using Serilog.Core;
using Serilog.Parsing;

namespace HashBackup.Utils;

/// <summary>
/// Serilog-Destrukturierer für sensible Daten, der diese vor dem Schreiben in Logs maskiert
/// </summary>
public class SensitiveDataDestructurer(ILogEventSink innerSink) : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        var maskedMessage = SensitiveDataManager.MaskSensitiveData(logEvent.MessageTemplate.Text);
        if (maskedMessage != logEvent.MessageTemplate.Text)
        {
            // Erstelle eine neue MessageTemplate mit maskiertem Text
            var parser = new MessageTemplateParser();
            var newTemplate = parser.Parse(maskedMessage);

            // Erstelle ein neues LogEvent mit der maskierten Nachricht
            logEvent = new LogEvent(
                logEvent.Timestamp,
                logEvent.Level,
                logEvent.Exception,
                newTemplate,
                logEvent.Properties.Select(p => new LogEventProperty(p.Key, p.Value)));
        }

        // Maskiere sensible Daten in formatierten Werten (Properties)
        var propertiesToUpdate = new Dictionary<string, LogEventPropertyValue>();

        foreach (var property in logEvent.Properties)
        {
            if (property.Value is ScalarValue { Value: string stringValue })
            {
                var maskedValue = SensitiveDataManager.MaskSensitiveData(stringValue);
                if (maskedValue != stringValue)
                {
                    propertiesToUpdate[property.Key] = new ScalarValue(maskedValue);
                }
            }
        }

        // Wenn Properties aktualisiert werden müssen, erstelle eine neue LogEvent-Instanz
        if (propertiesToUpdate.Count > 0)
        {
            var updatedProperties = new Dictionary<string, LogEventPropertyValue>(logEvent.Properties);
            foreach (var update in propertiesToUpdate)
            {
                updatedProperties[update.Key] = update.Value;
            }

            var properties = updatedProperties
                .Select(kvp => new LogEventProperty(kvp.Key, kvp.Value));

            logEvent = new LogEvent(
                logEvent.Timestamp,
                logEvent.Level,
                logEvent.Exception,
                logEvent.MessageTemplate,
                properties);
        }

        // Leite das maskierte LogEvent an den eigentlichen Sink weiter
        innerSink.Emit(logEvent);
    }
}