namespace InventoryService.Services;

/// Configuration for the shared secret that guards CreateInventoryItem, bound from the
/// "Provisioning" section. Supply it as the Provisioning__Token environment variable (or a user
/// secret); it is never committed.
public class ProvisioningOptions
{
    public const string SectionName = "Provisioning";

    /// The value callers must send as the "x-provisioning-token" call metadata. When it is empty,
    /// provisioning is disabled and every call is refused: the safe default is closed, not open.
    public string? Token { get; set; }
}
