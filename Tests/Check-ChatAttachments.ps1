# Run after dotnet build Build/CodexGUI.csproj --no-restore, with pwsh -NoProfile -STA -File Tests/Check-ChatAttachments.ps1.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot '../Build/bin/Debug/net10.0-windows/CodexGUI.dll'))
$type = $assembly.GetType('CodexGUI.MainWindow', $true)
$static = [Reflection.BindingFlags]'NonPublic,Static'
$instance = [Reflection.BindingFlags]'NonPublic,Instance'
function Require($condition, [string]$message) { if (-not $condition) { throw $message } }
$split = $type.GetMethod('SplitPromptWithAttachments', $static)
$parsed = $split.Invoke($null, @("Первый текст`nФайл для контекста: C:\screens\one.png`nВторой текст`n[Изображение: C:\screens\two.png]`n"))
Require ($parsed.Item2.Count -eq 2) 'Interleaved/legacy attachments were lost'
Require ($parsed.Item1 -eq "Первый текст`r`nВторой текст") 'Attachment markers leaked into message text'
$history = [Text.Json.JsonDocument]::Parse('{"content":[{"type":"localImage","path":"C:\\screens\\native.png"},{"type":"text","text":"Текст после изображения"}]}')
try {
    $prompt = $type.GetMethod('ReadUserMessageText', $static).Invoke($null, @($history.RootElement))
    $parsed = $split.Invoke($null, @($prompt))
    Require ($parsed.Item2.Count -eq 1 -and $parsed.Item2[0] -eq 'C:\screens\native.png') 'Native history image was lost'
    Require ($parsed.Item1 -eq 'Текст после изображения') 'Native image history text changed'
} finally { $history.Dispose() }
$owner = [Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject($type)
foreach ($field in @('_promptQueue', '_composerAttachments')) {
    $info = $type.GetField($field, $instance)
    $info.SetValue($owner, [Activator]::CreateInstance($info.FieldType))
}
$threadType = $type.GetNestedType('ChatThread', [Reflection.BindingFlags]::NonPublic)
$thread = [Activator]::CreateInstance($threadType); $thread.Id = 'selected'
$type.GetField('_currentThreadValue', $instance).SetValue($owner, $thread)
$messageType = $type.GetNestedType('ChatMessage', [Reflection.BindingFlags]::NonPublic)
$thread.Messages.Add([Activator]::CreateInstance($messageType, @($true, 'Файл для контекста: C:\screens\sent.png')))
$thread.Messages.Add([Activator]::CreateInstance($messageType, @($false, 'Файл для контекста: C:\screens\assistant.png')))
$queue = $type.GetField('_promptQueue', $instance).GetValue($owner)
$queuedType = $type.GetNestedType('QueuedPrompt', [Reflection.BindingFlags]::NonPublic)
foreach ($entry in @(@('selected','queued.png'), @('other','other.png'), @('selected','SENT.png'))) {
    $item = [Activator]::CreateInstance($queuedType); $item.ThreadId = $entry[0]
    $item.Text = 'Файл для контекста: C:\screens\' + $entry[1]; $queue.Add($item)
}
$draft = $type.GetField('_composerAttachments', $instance).GetValue($owner); $draft.Add('C:\screens\draft.png')
$paths = $type.GetMethod('GetChatAttachments', $instance).Invoke($owner, @())
Require ($paths.Count -eq 3) 'Expected sent, queued and draft attachments without duplicates'
Require ($paths -contains 'C:\screens\sent.png' -and $paths -contains 'C:\screens\queued.png' -and $paths -contains 'C:\screens\draft.png') 'Attachment source isolation failed'
$queue.Clear(); $draft.Clear()
$paths = $type.GetMethod('GetChatAttachments', $instance).Invoke($owner, @())
Require ($paths.Count -eq 1) 'Removed queued/draft attachments remained in the list'
'PASS: history images, legacy/interleaved markers, sent/queued/draft aggregation, deduplication and chat isolation'
