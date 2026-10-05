namespace Maki.Core.Sources;

/// <summary>
/// The site answered, successfully at the HTTP level, with an error of its own (MANGA Plus's error
/// popup, an <c>{"error": true}</c> body). The message is the site's own text, which is what the
/// download queue shows as the failure's detail.
/// </summary>
public class SourceErrorException(string message) : InvalidOperationException(message);
