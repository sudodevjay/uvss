using Basler.Pylon;
var list = CameraFinder.Enumerate();
Console.WriteLine($"Cameras found: {list.Count}");
foreach (var info in list)
{
    Console.WriteLine("-----");
    foreach (var k in new[]{CameraInfoKey.ModelName, CameraInfoKey.SerialNumber, CameraInfoKey.DeviceType, CameraInfoKey.FriendlyName})
        Console.WriteLine($"{k}: {(info.ContainsKey(k) ? info[k] : "")}");
    foreach (var k in new[]{"IpAddress","SubnetMask","DefaultGateway","Interface","MacAddress","DeviceVersion","DeviceAccessibility"})
        if (info.ContainsKey(k)) Console.WriteLine($"{k}: {info[k]}");
}
if (list.Count > 0 && args.Contains("--grab"))
{
    using var cam = new Camera(list[0]);
    cam.Open();
    var p = cam.Parameters;
    void Show(string n, Func<string> f){ try{ Console.WriteLine($"{n}: {f()}"); } catch(Exception e){ Console.WriteLine($"{n}: n/a ({e.Message})"); } }
    Show("Width/Height", () => $"{p[PLCamera.Width].GetValue()}x{p[PLCamera.Height].GetValue()} (max {p[PLCamera.WidthMax].GetValue()}x{p[PLCamera.HeightMax].GetValue()})");
    Show("Offset", () => $"x={p[PLCamera.OffsetX].GetValue()} y={p[PLCamera.OffsetY].GetValue()}");
    Show("PixelFormat", () => p[PLCamera.PixelFormat].GetValue());
    Show("ExposureTime", () => p[PLCamera.ExposureTime].GetValue().ToString());
    Show("ExposureTimeAbs", () => p[PLCamera.ExposureTimeAbs].GetValue().ToString());
    Show("ResultingFrameRate", () => p[PLCamera.ResultingFrameRate].GetValue().ToString());
    Show("ResultingFrameRateAbs", () => p[PLCamera.ResultingFrameRateAbs].GetValue().ToString());
    Show("PacketSize", () => p[PLCamera.GevSCPSPacketSize].GetValue().ToString());
    if(args.Contains("--auto")){
      p[PLCamera.UserSetSelector].SetValue("Default"); p[PLCamera.UserSetLoad].Execute();
      p[PLCamera.AutoFunctionProfile].SetValue("MinimizeGain"); p[PLCamera.AutoTargetBrightness].SetValue(0.5);
      p[PLCamera.AutoExposureTimeLowerLimit].SetValue(100.0); p[PLCamera.AutoExposureTimeUpperLimit].SetValue(2000.0);
      p[PLCamera.AutoGainLowerLimit].SetValue(0.0); p[PLCamera.AutoGainUpperLimit].SetValue(24.0);
      p[PLCamera.ExposureAuto].SetValue("Continuous"); p[PLCamera.GainAuto].SetValue("Continuous");
      p[PLCamera.PixelFormat].SetValue("Mono8"); p[PLCamera.GevSCPSPacketSize].SetValue(8192);
    }
    if(args.Contains("--dump")){
      foreach(var n in new[]{"OffsetX","OffsetY","BinningHorizontal","BinningVertical","DecimationHorizontal","DecimationVertical","ReverseX","ReverseY","TriggerSelector","TriggerMode","TriggerSource","ExposureMode","ExposureAuto","ExposureTime","AutoExposureTimeLowerLimit","AutoExposureTimeUpperLimit","GainAuto","Gain","AutoGainLowerLimit","AutoGainUpperLimit","AutoTargetBrightness","AutoFunctionProfile","BlackLevel","Gamma","AcquisitionFrameRateEnable","AcquisitionFrameRate","SensorReadoutTime","UserSetSelector","UserSetDefault","DeviceTemperature","LightSourcePreset","PixelFormat","BslLightControlMode"}){
        var prm=p[n]; Show(n, ()=> prm.IsReadable ? prm.ToString() : "(not readable)"); }
      foreach(var n in new[]{"Min","Max"}) Show("ExposureTime"+n, ()=> n=="Min"? p[PLCamera.ExposureTime].GetMinimum().ToString(): p[PLCamera.ExposureTime].GetMaximum().ToString());
      Show("GainMax", ()=> p[PLCamera.Gain].GetMaximum().ToString());
    }
    string Arg(string n,string d){var i=Array.IndexOf(args,n);return i>=0&&i+1<args.Length?args[i+1]:d;}
    try{ p[PLCamera.GevSCPD].SetValue(long.Parse(Arg("--ipd","0"))); }catch(Exception e){Console.WriteLine("GevSCPD: "+e.Message);}
    var fps=double.Parse(Arg("--fps","0"));
    if(fps>0){ p[PLCamera.AcquisitionFrameRateEnable].SetValue(true); p[PLCamera.AcquisitionFrameRate].SetValue(fps); }
    var pf=Arg("--pf",""); if(pf!="") p[PLCamera.PixelFormat].SetValue(pf);
    cam.Parameters[PLCameraInstance.MaxNumBuffer].SetValue(32);
    var w=Arg("--w",""); if(w!=""){ p[PLCamera.OffsetX].SetValue(0); p[PLCamera.OffsetY].SetValue(0); p[PLCamera.Width].SetValue(long.Parse(w)); p[PLCamera.Height].SetValue(long.Parse(Arg("--h","480"))); }
    var ps=Arg("--ps",""); if(ps!="") p[PLCamera.GevSCPSPacketSize].SetValue(long.Parse(ps));
    var drv=Arg("--drv",""); if(drv!="") { try{ cam.Parameters[PLStream.Type].SetValue(drv);}catch(Exception e){Console.WriteLine("drv: "+e.Message);} }
    var tl=Arg("--tl",""); if(tl!=""){ p[PLCamera.DeviceLinkThroughputLimitMode].SetValue("On"); p[PLCamera.DeviceLinkThroughputLimit].SetValue(long.Parse(tl)); }
    Show("LinkSpeed", () => p[PLCamera.GevLinkSpeed].GetValue().ToString());
    Show("ThroughputLimitMode", () => p[PLCamera.DeviceLinkThroughputLimitMode].GetValue());
    Show("ThroughputLimit", () => p[PLCamera.DeviceLinkThroughputLimit].GetValue().ToString());
    Show("CurrentThroughput", () => p[PLCamera.DeviceLinkCurrentThroughput].GetValue().ToString());
    if(args.Contains("--robust")){ var sp=cam.Parameters;
      try{ sp[PLStream.FrameRetention].SetValue(2000);}catch(Exception e){Console.WriteLine("FR "+e.Message);}
      try{ sp[PLStream.PacketTimeout].SetValue(200);}catch(Exception e){Console.WriteLine("PT "+e.Message);}
      try{ sp[PLStream.ResendTimeout].SetValue(10);}catch(Exception e){Console.WriteLine("RT "+e.Message);}
      try{ sp[PLStream.ResendRequestResponseTimeout].SetValue(20);}catch(Exception e){Console.WriteLine("RRRT "+e.Message);}
      try{ sp[PLStream.MaximumNumberResendRequests].SetValue(100);}catch(Exception e){Console.WriteLine("MNRR "+e.Message);}
      try{ sp[PLStream.ReceiveThreadPriorityOverride].SetValue(true); sp[PLStream.ReceiveThreadPriority].SetValue(25);}catch(Exception e){Console.WriteLine("Prio "+e.Message);}
    }
    Show("StreamType", () => cam.Parameters[PLStream.Type].GetValue());
    Show("GevSCPD", () => p[PLCamera.GevSCPD].GetValue().ToString());
    Show("ResultingFrameRate(now)", () => p[PLCamera.ResultingFrameRate].GetValue().ToString());
    Show("PixelFormat(now)", () => p[PLCamera.PixelFormat].GetValue());
    cam.StreamGrabber.Start();
    if(args.Contains("--settle")){ for(int i=0;i<60;i++){ using var r0 = cam.StreamGrabber.RetrieveResult(2000, TimeoutHandling.Return); } Show("After auto: ExposureTime", ()=>p[PLCamera.ExposureTime].GetValue().ToString()); Show("After auto: Gain", ()=>p[PLCamera.Gain].GetValue().ToString()); }
    int ok=0, fail=0; var sw = System.Diagnostics.Stopwatch.StartNew();
    for (int i=0;i<int.Parse(Arg("--n","50"));i++){ using var r = cam.StreamGrabber.RetrieveResult(2000, TimeoutHandling.Return); if (r!=null && r.GrabSucceeded) { ok++; if (ok==1){ ImagePersistence.Save(ImageFileFormat.Png, Path.Combine(AppContext.BaseDirectory,"frame.png"), r); Console.WriteLine($"First frame {r.Width}x{r.Height} {r.PixelTypeValue} saved"); } } else { fail++; if (r!=null && fail<=2) Console.WriteLine($"Grab failed: {r.ErrorDescription}"); } }
    cam.StreamGrabber.Stop();
    Show("FailedBuffers", () => cam.Parameters[PLStream.Statistic_Failed_Buffer_Count].GetValue().ToString()); Show("FailedPackets", () => cam.Parameters[PLStream.Statistic_Failed_Packet_Count].GetValue().ToString()); Show("TotalPackets", () => cam.Parameters[PLStream.Statistic_Total_Packet_Count].GetValue().ToString()); Show("ResendReq", () => cam.Parameters[PLStream.Statistic_Resend_Request_Count].GetValue().ToString()); Show("LastFailedId", () => cam.Parameters[PLStream.Statistic_Last_Failed_Buffer_Status_Text].GetValue());
    Console.WriteLine($"Grabbed ok={ok} fail={fail} in {sw.ElapsedMilliseconds}ms (~{ok*1000.0/sw.ElapsedMilliseconds:F1} fps)");
}
