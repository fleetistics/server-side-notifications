using exs.notifications_service.Sender;
using exs.notifications_service.Tests.Sender.Support;
using Shouldly;

namespace exs.notifications_service.Tests.Sender
{
	public class RelayBackoffTests
	{
		private readonly ManualTimeProvider mClock = new();

		public class RowCooldownTests
		{
			private readonly ManualTimeProvider mClock = new();

			private RowCooldown create(int maxSeconds = 60) => new(TimeSpan.FromSeconds(maxSeconds), mClock);

			[Fact]
			public void ANewRow_IsNotCooling()
			{
				create().CoolingIds().ShouldBeEmpty();
				create().TimeUntilNextExpiry().ShouldBeNull();
			}

			[Fact]
			public void AfterAFault_TheRowIsHeldBack_UntilTheDelayPasses()
			{
				var cooldown = create();
				cooldown.RegisterFault(7);

				cooldown.CoolingIds().ShouldContain(7L);
				mClock.Advance(TimeSpan.FromSeconds(1.9));
				cooldown.CoolingIds().ShouldContain(7L);
				mClock.Advance(TimeSpan.FromSeconds(0.2));
				cooldown.CoolingIds().ShouldNotContain(7L);
			}

			[Fact]
			public void EachRepeatedFault_WaitsLonger_UpToTheCap()
			{
				var cooldown = create(maxSeconds: 10);

				cooldown.RegisterFault(7); // 2s
				mClock.Advance(TimeSpan.FromSeconds(2.1));
				cooldown.RegisterFault(7); // 4s
				mClock.Advance(TimeSpan.FromSeconds(3.9));
				cooldown.CoolingIds().ShouldContain(7L);

				for (var i = 0; i < 40; i++) cooldown.RegisterFault(7); // far past where 2^n would overflow
				mClock.Advance(TimeSpan.FromSeconds(9.9));
				cooldown.CoolingIds().ShouldContain(7L);
				mClock.Advance(TimeSpan.FromSeconds(0.2));
				cooldown.CoolingIds().ShouldNotContain(7L);
			}

			[Fact]
			public void Clear_ForgetsTheRowAndItsFaultCount()
			{
				var cooldown = create();
				cooldown.RegisterFault(7);
				cooldown.RegisterFault(7);

				cooldown.Clear(7);
				cooldown.CoolingIds().ShouldBeEmpty();

				cooldown.RegisterFault(7); // back to the shortest delay
				mClock.Advance(TimeSpan.FromSeconds(2.1));
				cooldown.CoolingIds().ShouldBeEmpty();
			}

			[Fact]
			public void TimeUntilNextExpiry_IsTheEarliestStillRunning()
			{
				var cooldown = create();
				cooldown.RegisterFault(1);                    // ready at +2s
				mClock.Advance(TimeSpan.FromSeconds(1));
				cooldown.RegisterFault(2);                    // ready at +3s
				mClock.Advance(TimeSpan.FromSeconds(1.5));    // row 1 is eligible again

				cooldown.TimeUntilNextExpiry().ShouldBe(TimeSpan.FromSeconds(0.5));
				mClock.Advance(TimeSpan.FromSeconds(1));
				cooldown.TimeUntilNextExpiry().ShouldBeNull();
			}
		}

		public class TransportGateTests
		{
			private readonly ManualTimeProvider mClock = new();

			private TransportGate create(int maxSeconds = 60) => new(TimeSpan.FromSeconds(maxSeconds), mClock);

			[Fact]
			public void StartsOpen()
			{
				create().IsBlocked.ShouldBeFalse();
				create().TimeUntilOpen().ShouldBeNull();
			}

			[Fact]
			public void AFailure_ClosesItForAWhile_ThenItReopensByItself()
			{
				var gate = create();
				gate.RegisterFailure();

				gate.IsBlocked.ShouldBeTrue();
				gate.TimeUntilOpen().ShouldBe(TimeSpan.FromSeconds(2));
				mClock.Advance(TimeSpan.FromSeconds(1.9));
				gate.IsBlocked.ShouldBeTrue();
				mClock.Advance(TimeSpan.FromSeconds(0.2));
				gate.IsBlocked.ShouldBeFalse();
			}

			[Fact]
			public void RepeatedFailures_WaitLonger_UpToTheCap()
			{
				var gate = create(maxSeconds: 10);

				gate.RegisterFailure(); // 2s
				gate.RegisterFailure(); // 4s
				gate.TimeUntilOpen().ShouldBe(TimeSpan.FromSeconds(4));
				for (var i = 0; i < 40; i++) gate.RegisterFailure();
				gate.TimeUntilOpen().ShouldBe(TimeSpan.FromSeconds(10));
			}

			[Fact]
			public void ASuccess_OpensItAtOnce_AndResetsTheEscalation()
			{
				var gate = create();
				gate.RegisterFailure();
				gate.RegisterFailure();
				gate.RegisterFailure();

				gate.RegisterSuccess();

				gate.IsBlocked.ShouldBeFalse();
				gate.RegisterFailure();
				gate.TimeUntilOpen().ShouldBe(TimeSpan.FromSeconds(2)); // not 16
			}
		}
	}
}
