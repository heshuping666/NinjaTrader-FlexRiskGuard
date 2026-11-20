using System;
using System.IO;
using NinjaTrader.NinjaScript.AddOns;
class Tests {
 static void Assert(bool ok) { if(!ok) throw new Exception("Assertion failed"); }
 static void Main() {
  string path=Path.Combine(Path.GetTempPath(), "FlexRiskGuard-test-"+Guid.NewGuid()+".xml");
  var store=new P0Store(path);
  store.Change("Sim101","lock",30);
  Assert(new P0Store(path).Status("Sim101")=="账户手动锁定");
  Assert(store.Status("Sim102")=="内部未锁定 · 仅监控");
  store.Change("Sim101","globalLock",30);
  Assert(new P0Store(path).Status("Sim102")=="全局手动锁定");
  store.Change("Sim101","globalResume",30);
  Assert(store.Status("Sim101")=="账户手动锁定");
  store.Change("Sim101","resume",30);
  store.Change("Sim101","violation",30);
  Assert(store.Status("Sim102").StartsWith("全局冷静期"));
  bool refused=false; try{store.Change("Sim102","resume",30);}catch(InvalidOperationException){refused=true;} Assert(refused);
  store.Change("Sim102","violation",30);
  Assert(new P0Store(path).Status("Sim101").StartsWith("全局当日锁定"));
  refused=false;try{store.Change("Live","lock",30);}catch(InvalidOperationException){refused=true;}Assert(refused);
  File.WriteAllText(path,"corrupt"); Assert(store.Status("Sim101").StartsWith("P0 状态不可靠"));
  Console.WriteLine("PASS: persistence, isolation, global lock, cross-account cooldown, early resume rejection, second violation, SIM-only, corrupt state");
 }
}

