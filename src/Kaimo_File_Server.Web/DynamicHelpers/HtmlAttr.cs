namespace Kaimo_File_Server.Web.DynamicHelpers;

/// <summary>
/// Splattable attribute sets that stop browsers and password-manager extensions from
/// autofilling admin create/edit user fields — those extensions largely ignore
/// <c>autocomplete</c>, so the vendor opt-out data-attributes are included too. The login
/// form deliberately does NOT use these, so credential autofill still works there.
/// </summary>
public static class HtmlAttr
{
    /// <summary>For non-password fields (username, e-mail) that should not be autofilled.</summary>
    public static IReadOnlyDictionary<string, object> NoAutofill { get; } = new Dictionary<string, object>
    {
        ["autocomplete"] = "off",
        ["data-form-type"] = "other",
        ["data-1p-ignore"] = "true",   // 1Password
        ["data-lpignore"] = "true",    // LastPass
        ["data-bwignore"] = "true",    // Bitwarden
    };

    /// <summary>For password fields. We use <c>off</c> rather than <c>new-password</c>
    /// on purpose: <c>new-password</c> makes Chromium/Brave treat the panel as a sign-up
    /// form and offer to generate/save a password (the "password alias" popup) on the
    /// adjacent text fields. <c>off</c> plus the vendor opt-outs suppresses both autofill
    /// and that generation offer.</summary>
    public static IReadOnlyDictionary<string, object> NoAutofillPassword { get; } = new Dictionary<string, object>
    {
        ["autocomplete"] = "off",
        ["data-form-type"] = "other",
        ["data-1p-ignore"] = "true",
        ["data-lpignore"] = "true",
        ["data-bwignore"] = "true",
    };
}
