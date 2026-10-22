[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [AllowEmptyString()]
    [string]$Value
)

# MSBuild treats commas and semicolons in a command-line property as
# separators between multiple properties. Percent-escape them at the process
# boundary; MSBuild restores the literal characters when it reads the value.
$Value.
    Replace('%', '%25').
    Replace(',', '%2C').
    Replace(';', '%3B')
