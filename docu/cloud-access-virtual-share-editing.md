# Cloud Access editing

Cloud Access administrators can edit both connection display names and virtual-share settings from the Cloud Access page.

## Connection editing

Only the display name is editable. The provider, department assignment, authorization state, and encrypted credentials remain unchanged. Names are trimmed, must not be blank, and are limited to 200 characters.

## Virtual-share editing

An administrator with `ManageCloudAccess` permission for the share's department can change the share name, access mode, and remote folder. Saving a remote-folder change resolves the selected OneDrive folder again and stores both its normalized path and provider item ID. The share identity and all existing user/group grants are preserved.

Share names must remain valid Samba share names and unique across local and virtual shares, case-insensitively. A virtual share may retain its current name.

## Share list columns

The embedded Virtual Shares view lists each share with a **Name**, a **Status**, and its **Access mode**. The Status column reuses the local-share availability indicator: a coloured dot with a label that reads *Available* when the share is enabled and its backing connection is ready, *Not available* when the connection is missing or not ready, and *Disabled* when the share itself has been disabled. Green marks a usable share, red an unusable one.

A **Show details** toggle in the page header (mirroring the local-shares toggle) expands the list with an additional **Connection** column that names the backing storage connection for each row. The toggle is only offered when at least one virtual share exists. On narrow viewports the list collapses to Name and Status.

## Access list

The Access tab lets administrators add or remove the eligible users and groups for the share's department, then persist the complete list. Changes to the access list are authorized independently and do not require recreating the virtual share.

All labels, actions, descriptions, and validation messages use the Cloud Access resource keys in `Resources.resx` with a German localization in `Resources.de.resx`.
