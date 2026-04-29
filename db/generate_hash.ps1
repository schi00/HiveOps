Add-Type -AssemblyName System.Security
$salt = [byte[]]::new(16)
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($salt)
$pbkdf2 = New-Object Security.Cryptography.Rfc2898DeriveBytes('123456', $salt, 100000, [Security.Cryptography.HashAlgorithmName]::SHA256)
$key = $pbkdf2.GetBytes(32)
$hash = "v1.100000.$([Convert]::ToBase64String($salt)).$([Convert]::ToBase64String($key))"
Write-Output $hash
