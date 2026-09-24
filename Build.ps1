<#
    一键编译“空闲自动关闭程序”。

    产物只有一个文件：dist\IdleAppCloser.exe
    双击即可运行；目标电脑不需要安装 .NET SDK 或任何运行时
    （用的是 Windows 自带的 .NET Framework 编译器）。

    用法：
        powershell -ExecutionPolicy Bypass -File .\Build.ps1
#>

$ErrorActionPreference = 'Stop'

$root      = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir    = Join-Path $root 'src'
$assetsDir = Join-Path $root 'assets'
$distDir   = Join-Path $root 'dist'

function Write-Step { param([string]$Text) Write-Host ''; Write-Host ">>> $Text" -ForegroundColor Cyan }
function Write-Ok   { param([string]$Text) Write-Host "    $Text" -ForegroundColor Green }
function Write-Note { param([string]$Text) Write-Host "    $Text" -ForegroundColor Yellow }

Write-Host ''
Write-Host '=============================================' -ForegroundColor DarkCyan
Write-Host '  编译 空闲自动关闭程序' -ForegroundColor White
Write-Host '=============================================' -ForegroundColor DarkCyan

# ---------------------------------------------------------------- 1. 检查环境
Write-Step '检查源码与编译器'

$sources = @(Get-ChildItem (Join-Path $srcDir '*.cs') -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) { throw "在 src\ 里没有找到任何 .cs 源文件。" }
Write-Ok ("找到 {0} 个源文件" -f $sources.Count)

$csc = $null
foreach ($candidate in @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)) {
    if (Test-Path $candidate) { $csc = $candidate; break }
}
if (-not $csc) { throw '找不到 csc.exe，请确认系统装了 .NET Framework 4.x。' }
Write-Ok "编译器: $csc"

$fwDir = Split-Path $csc -Parent
$refs = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll') |
        ForEach-Object { Join-Path $fwDir $_ }
foreach ($r in $refs) { if (-not (Test-Path $r)) { throw "缺少引用程序集: $r" } }
Write-Ok ("引用程序集: {0} 个" -f $refs.Count)

if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir -Force | Out-Null }
if (-not (Test-Path $assetsDir)) { New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null }

# ---------------------------------------------------------------- 2. 生成图标
Write-Step '生成程序图标'

$icoPath = Join-Path $assetsDir 'app.ico'

function New-AppIcon {
    param([string]$OutPath)

    Add-Type -AssemblyName System.Drawing

    # 图案：深蓝圆底 + 白色电源符号（一弧一线），语义就是“关闭”
    $sizes   = @(16, 32, 48, 256)
    $pngList = New-Object System.Collections.ArrayList

    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap -ArgumentList $s, $s
        $g   = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.Clear([System.Drawing.Color]::Transparent)

            $w = [double]$s

            $bg = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(255, 28, 88, 168))
            try { $g.FillEllipse($bg, [float]0.5, [float]0.5, [float]($w - 1.5), [float]($w - 1.5)) }
            finally { $bg.Dispose() }

            $penW = [float][Math]::Max(1.0, $w * 0.11)
            $pen  = New-Object System.Drawing.Pen -ArgumentList ([System.Drawing.Color]::White), $penW
            try {
                $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
                $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round

                $cx = $w / 2.0
                $cy = $w / 2.0
                $r  = $w * 0.30

                $rect = New-Object System.Drawing.RectangleF -ArgumentList @(
                    [float]($cx - $r), [float]($cy - $r), [float](2 * $r), [float](2 * $r)
                )
                # 从 110 度起画 320 度，右上角正好留出缺口
                $g.DrawArc($pen, $rect, [float]110.0, [float]320.0)
                $g.DrawLine($pen, [float]$cx, [float]($cy - $r * 1.45), [float]$cx, [float]($cy - $r * 0.15))
            }
            finally { $pen.Dispose() }
        }
        finally { $g.Dispose() }

        $ms = New-Object System.IO.MemoryStream
        try {
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            [void]$pngList.Add(@{ Size = $s; Data = $ms.ToArray() })
        }
        finally {
            $ms.Dispose()
            $bmp.Dispose()
        }
    }

    # 手工拼 ICO 容器（Vista+ 支持内嵌 PNG；一张 ico 里放多个尺寸，各场景都清晰）
    $fs = [System.IO.File]::Create($OutPath)
    try {
        $bw = New-Object System.IO.BinaryWriter -ArgumentList $fs
        try {
            $bw.Write([UInt16]0)                    # reserved
            $bw.Write([UInt16]1)                    # type = icon
            $bw.Write([UInt16]$pngList.Count)       # 图像数量

            $offset = 6 + 16 * $pngList.Count
            foreach ($item in $pngList) {
                $dim = [Byte]0                               # 0 表示 256
                if ($item.Size -lt 256) { $dim = [Byte]$item.Size }
                $bw.Write($dim)                              # width
                $bw.Write($dim)                              # height
                $bw.Write([Byte]0)                           # 调色板数量
                $bw.Write([Byte]0)                           # reserved
                $bw.Write([UInt16]1)                         # color planes
                $bw.Write([UInt16]32)                        # bits per pixel
                $bw.Write([UInt32]$item.Data.Length)         # 数据长度
                $bw.Write([UInt32]$offset)                   # 数据偏移
                $offset += $item.Data.Length
            }
            foreach ($item in $pngList) { $bw.Write($item.Data) }
        }
        finally { $bw.Dispose() }
    }
    finally { $fs.Dispose() }
}

$useIcon = $false
try {
    New-AppIcon -OutPath $icoPath
    if (Test-Path $icoPath) {
        $useIcon = $true
        Write-Ok ("已生成图标: app.ico ({0:N1} KB)" -f ((Get-Item $icoPath).Length / 1KB))
    }
}
catch {
    Write-Note "图标生成失败（不影响编译，将使用系统默认图标）: $($_.Exception.Message)"
}

# ---------------------------------------------------------------- 3. 编译
Write-Step '编译'

$exePath = Join-Path $distDir 'IdleAppCloser.exe'

$argList = @(
    '/target:winexe',           # 无控制台窗口 —— 双击时不闪黑框
    "/out:$exePath",
    '/nologo',
    '/optimize+',
    '/platform:anycpu',
    '/codepage:65001',          # 源码是 UTF-8，不加这个中文串会乱码
    '/utf8output',
    '/warn:4'
)
foreach ($r in $refs) { $argList += "/r:$r" }

$manifest = Join-Path $srcDir 'app.manifest'
if (Test-Path $manifest) { $argList += "/win32manifest:$manifest" }
if ($useIcon) { $argList += "/win32icon:$icoPath" }

$argList += $sources

# 先删掉旧的输出文件，这样“到底有没有编译出来”才能作为可靠判据
if (Test-Path $exePath) { Remove-Item $exePath -Force }

Write-Host "    编译中 ..." -NoNewline

# 注意：千万不要写成 "& $csc ... 2>&1"。
# PowerShell 5.1 用 2>&1 合并原生命令的 stderr 时会生成 NativeCommandError，
# 碰上 $ErrorActionPreference='Stop' 会让整个赋值失败 —— 结果是既拿不到 $LASTEXITCODE、
# 也看不到编译器的真实报错，只留下一句“编译失败（退出码 ）”，极难排查。
# 这里只捕获 stdout、让 stderr 直通控制台：csc 的诊断本来就写 stdout，两不耽误。
$previousEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $output = & $csc $argList
    $code   = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousEap
}
$output = @($output | Where-Object { $_ -and $_.ToString().Trim().Length -gt 0 })

# 判据：以“输出文件是否真的生成”为主，退出码为辅。
# 只认退出码在个别环境下会误判 —— 拿不到 $LASTEXITCODE 时它是空值，
# 会让明明编译成功的构建被报成失败。
$built  = Test-Path $exePath
$failed = (-not $built) -or (($code -ne $null) -and ($code -ne 0))

if ($failed) {
    Write-Host ' 失败' -ForegroundColor Red
    Write-Host ''
    if ($output.Count -gt 0) {
        $output | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
    }
    else {
        Write-Host '    （编译器没有返回任何错误信息，请检查 csc.exe 是否被杀软拦截）' -ForegroundColor Red
    }
    throw "编译失败：IdleAppCloser.exe 没有生成（退出码 $code）"
}

if ($output.Count -gt 0) {
    Write-Host ' 有警告' -ForegroundColor Yellow
    $output | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
}
else {
    Write-Host ' 成功' -ForegroundColor Green
}
Write-Ok ("dist\IdleAppCloser.exe  ({0:N1} KB)" -f ((Get-Item $exePath).Length / 1KB))

# ---------------------------------------------------------------- 4. 自检
Write-Step '自检'

$checkFile = Join-Path $env:TEMP ('idle-check-' + [Guid]::NewGuid().ToString('N') + '.txt')
try {
    if (Test-Path $checkFile) { Remove-Item $checkFile -Force }

    # 让程序把自检结果写进文件再读回来。
    # 比捕获子进程 stdout 可靠得多：个别受限环境会禁掉子进程管道，
    # 那时既拿不到输出也拿不到退出码，而写文件不受影响。
    $previousEap2 = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $exePath '--check' "--check-out=$checkFile" | Out-Null }
    finally { $ErrorActionPreference = $previousEap2 }

    if (Test-Path $checkFile) {
        Write-Ok '程序可以正常运行：'
        Write-Host ''
        Get-Content $checkFile -Encoding UTF8 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    }
    else {
        Write-Note '自检没有拿到结果，请手动双击 dist\IdleAppCloser.exe 打开界面确认。'
    }
}
catch {
    Write-Note "自检未通过: $($_.Exception.Message)"
}
finally {
    try { if (Test-Path $checkFile) { Remove-Item $checkFile -Force } } catch { }
}

# ---------------------------------------------------------------- 5. 使用说明
Write-Step '使用说明'

# 把 README 一并放进 dist，这样发给别人时就是一个完整的包：一个 exe 加一份说明，
# 对方拿到手不用再问"这东西怎么用"。
$readmePath = Join-Path $root 'README.md'
$readmeOut  = Join-Path $distDir '使用说明.txt'
if (Test-Path $readmePath) {
    $readmeText = [System.IO.File]::ReadAllText($readmePath, [System.Text.Encoding]::UTF8)
    # 写成带 BOM 的 UTF-8：记事本靠 BOM 判断编码，没有它中文会整篇变成乱码
    [System.IO.File]::WriteAllText($readmeOut, $readmeText, (New-Object System.Text.UTF8Encoding($true)))
    Write-Ok ("dist\使用说明.txt  ({0:N1} KB)" -f ((Get-Item $readmeOut).Length / 1KB))
}
else {
    Write-Note '没有找到 README.md，跳过使用说明'
}

# 自检会真的把程序跑一次，顺带在 dist 里留下一个 logs 目录。
# 交付出去的要是个干净的包，让用户第一次运行时自己去生成。
$selfTestLogs = Join-Path $distDir 'logs'
if (Test-Path $selfTestLogs) { Remove-Item $selfTestLogs -Recurse -Force -ErrorAction SilentlyContinue }

# ---------------------------------------------------------------- 完成
Write-Host ''
Write-Host '=============================================' -ForegroundColor DarkCyan
Write-Host '  编译完成' -ForegroundColor Green
Write-Host '=============================================' -ForegroundColor DarkCyan
Write-Host ''
Write-Host '把 dist 文件夹整个拷给别人即可：' -ForegroundColor White
Write-Host '    dist\IdleAppCloser.exe   ← 程序本体，就这一个文件' -ForegroundColor Cyan
Write-Host '    dist\使用说明.txt         ← 给对方看的用法说明' -ForegroundColor Cyan
Write-Host ''
Write-Host '对方双击打开，填上程序名和时长，点“保存并开始监控”就好了。' -ForegroundColor Gray
Write-Host '需要开机自动运行的话，把界面上的勾选框打上。' -ForegroundColor Gray
Write-Host ''
