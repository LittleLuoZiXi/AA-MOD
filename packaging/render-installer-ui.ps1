param([Parameter(Mandatory=$true)][string]$CorePath,[Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
[Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)
$assembly=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($CorePath))
$formType=$assembly.GetTypes() | Where-Object {$_.BaseType -eq [Windows.Forms.Form]} | Select-Object -First 1
if(!$formType){throw 'Protected core contains no installer form.'}
$constructor=$formType.GetConstructor([type[]]@([string]))
$form=$constructor.Invoke([object[]]@('E:\AzureArchive_100_fix'))
try {
  if($form.Text -cne '剧本改稿同步 MOD  一键安装'){throw 'Installer title does not match requested name.'}
  $form.ShowInTaskbar=$false
  function Prepare($control){$null=$control.Handle;foreach($child in $control.Controls){Prepare $child};$control.PerformLayout()}
  Prepare $form
  # Set only the WinForms visibility state for DrawToBitmap; do not show an OS window.
  $setState=[Windows.Forms.Control].GetMethod('SetState',[Reflection.BindingFlags]'Instance,NonPublic')
  $null=$setState.Invoke($form,[object[]]@(2,$true))
  $form.PerformLayout()
  $bitmap=[Drawing.Bitmap]::new($form.Width,$form.Height)
  try {$form.DrawToBitmap($bitmap,[Drawing.Rectangle]::new(0,0,$bitmap.Width,$bitmap.Height));$bitmap.Save($OutputPath,[Drawing.Imaging.ImageFormat]::Png)}finally{$bitmap.Dispose()}
  [ordered]@{title=$form.Text;clientWidth=$form.ClientSize.Width;clientHeight=$form.ClientSize.Height;inputCore=$CorePath;render=$OutputPath;interactiveWindowShown=$false;installationInvoked=$false}|ConvertTo-Json
} finally {$form.Dispose()}
