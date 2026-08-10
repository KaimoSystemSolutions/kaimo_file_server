# Cloud Access editing

Cloud Access administrators can edit both connection display names and virtual-share settings from the Cloud Access page.

## Connection editing

Only the display name is editable. The provider, department assignment, authorization state, and encrypted credentials remain unchanged. Names are trimmed, must not be blank, and are limited to 200 characters.

## Virtual-share editing

An administrator with `ManageCloudAccess` permission for the share's department can change the share name, access mode, and remote folder. Saving a remote-folder change resolves the selected OneDrive folder again and stores both its normalized path and provider item ID. The share identity and all existing user/group grants are preserved.

Share names must remain valid Samba share names and unique across local and virtual shares, case-insensitively. A virtual share may retain its current name.

## Access list

The Access tab lets administrators add or remove the eligible users and groups for the share's department, then persist the complete list. Changes to the access list are authorized independently and do not require recreating the virtual share.

All labels, actions, descriptions, and validation messages use the Cloud Access resource keys in `Resources.resx` with a German localization in `Resources.de.resx`.
