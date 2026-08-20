using Microsoft.VisualStudio.TestTools.UnitTesting;
using PomodoroClock.Core;
using PomodoroClock.Infrastructure;

namespace PomodoroClock.Tests;

[TestClass]
public class TimerTests
{
    private sealed class FakeClock : IClock { public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow; public DateTimeOffset UtcNow => Now; }
    [TestMethod] public void CompleteFocusUsesTargetTimeAndAwaitsSettlement(){var clock=new FakeClock();var timer=new PomodoroTimer(clock,new TimerSettings(1,1,2,2));Assert.IsTrue(timer.Start());clock.Now=clock.Now.AddMinutes(1);Assert.AreEqual(TimerStatus.AwaitingSettlement,timer.Tick().Status);Assert.AreEqual(TimeSpan.Zero,timer.Snapshot.Remaining);}
    [TestMethod] public void CancelDoesNotComplete(){var timer=new PomodoroTimer(new FakeClock(),new TimerSettings(1,1,2,2));timer.Start();Assert.IsTrue(timer.Cancel());Assert.AreEqual(TimerStatus.Ready,timer.Snapshot.Status);Assert.AreEqual(0,timer.Snapshot.CompletedRounds);}
    [TestMethod] public void LongBreakAfterConfiguredRounds(){var clock=new FakeClock();var timer=new PomodoroTimer(clock,new TimerSettings(1,1,2,2));for(var i=0;i<2;i++){timer.Start();clock.Now=clock.Now.AddMinutes(1);timer.Tick();timer.CompleteFocusAndSetNextPhase(i==1);if(i==0){timer.Start();clock.Now=clock.Now.AddMinutes(1);timer.Tick();timer.FinishBreak();}}Assert.AreEqual(TimerPhase.LongBreak,timer.Snapshot.Phase);Assert.AreEqual(2,timer.Snapshot.CompletedRounds);}
    [TestMethod] public void NoPauseStateExists(){Assert.IsFalse(Enum.GetNames<TimerStatus>().Contains("Paused"));}
    [TestMethod] public void SettingsValidateRanges(){Assert.Throws<ArgumentOutOfRangeException>(()=>new TimerSettings(0,5,15,4).Validate());Assert.Throws<ArgumentOutOfRangeException>(()=>new TimerSettings(25,5,15,13).Validate());}
}

[TestClass]
public class SqliteTests
{
    [TestMethod] public async Task DatabasePersistsWorkItemAndSettlement(){var file=Path.Combine(Path.GetTempPath(),$"pomodoro-{Guid.NewGuid():N}.db");try{var db=new SqliteDatabase(file);var tasks=new SqliteWorkItemRepository(db);var item=new WorkItem(Guid.NewGuid(),"测试事项","",WorkItemStatus.InProgress,WorkItemPriority.High,DateTimeOffset.UtcNow,null,0);await tasks.SaveAsync(item);var sessions=new SqliteSessionRepository(db);var start=DateTimeOffset.UtcNow.AddMinutes(-25);await sessions.SaveSettlementAsync(new(Guid.NewGuid(),start,start.AddMinutes(25),1500),new[]{item.Id},null,1,false);var loaded=(await tasks.GetAsync()).Single();Assert.AreEqual(1,loaded.ParticipationCount);Assert.AreEqual(1,(await sessions.GetAsync(DateOnly.FromDateTime(DateTime.Now),DateOnly.FromDateTime(DateTime.Now))).Sessions.Count);}finally{try{File.Delete(file);}catch{}}}
    [TestMethod] public async Task DuplicateLinkDoesNotDoubleCount(){var file=Path.Combine(Path.GetTempPath(),$"pomodoro-{Guid.NewGuid():N}.db");try{var db=new SqliteDatabase(file);var tasks=new SqliteWorkItemRepository(db);var item=new WorkItem(Guid.NewGuid(),"事项","",WorkItemStatus.InProgress,WorkItemPriority.Normal,DateTimeOffset.UtcNow,null,0);await tasks.SaveAsync(item);var repo=new SqliteSessionRepository(db);var id=Guid.NewGuid();var s=new FocusSession(id,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddMinutes(1),60);await repo.SaveSettlementAsync(s,new[]{item.Id,item.Id},null,1,false);var loaded=(await tasks.GetAsync()).Single();Assert.AreEqual(1,loaded.ParticipationCount);}finally{try{File.Delete(file);}catch{}}}
    [TestMethod] public async Task EditingPriorityAndStatusUpdatesExistingItemWithoutDuplicate(){var file=Path.Combine(Path.GetTempPath(),$"pomodoro-{Guid.NewGuid():N}.db");try{var db=new SqliteDatabase(file);var tasks=new SqliteWorkItemRepository(db);var item=new WorkItem(Guid.NewGuid(),"待修改事项","",WorkItemStatus.InProgress,WorkItemPriority.Low,DateTimeOffset.UtcNow,null,0);await tasks.SaveAsync(item);var completed=DateTimeOffset.UtcNow;await tasks.SaveAsync(item with{Priority=WorkItemPriority.High,Status=WorkItemStatus.Completed,CompletedUtc=completed});var loaded=await tasks.GetAsync();Assert.HasCount(1,loaded);Assert.AreEqual(WorkItemPriority.High,loaded[0].Priority);Assert.AreEqual(WorkItemStatus.Completed,loaded[0].Status);Assert.IsNotNull(loaded[0].CompletedUtc);}finally{try{File.Delete(file);}catch{}}}
}
