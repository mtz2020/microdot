﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Gigya.Common.Contracts.HttpService;
using Gigya.Microdot.Fakes;
using Gigya.Microdot.Interfaces;
using Gigya.Microdot.Interfaces.SystemWrappers;
using Gigya.Microdot.ServiceDiscovery.Config;
using Gigya.Microdot.ServiceProxy;
using Gigya.Microdot.ServiceProxy.Caching;
using Gigya.Microdot.Testing.Shared;
using Gigya.Microdot.Testing.Shared.Utils;
using Gigya.ServiceContract.HttpService;
using Ninject;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Gigya.Common.Contracts.Attributes;

namespace Gigya.Microdot.UnitTests.Caching
{
    [TestFixture,Parallelizable(ParallelScope.Fixtures)]
    public class CachingProxyTests
    {
        const string FirstResult  = "First Result";
        const string SecondResult = "Second Result";

        private Dictionary<string, string> _configDic;
        private TestingKernel<ConsoleLog> _kernel;
        private ICachingTestService _proxy;
        private ICachingTestService _serviceMock;
        private DateTime _now;
        private string _serviceResult;
        private ICacheRevoker _cacheRevoker;
        private Task _revokeDelay;
        private IRevokeListener _revokeListener;

        [OneTimeSetUp]
        public void OneTimeSetup()
        { 
            _configDic = new Dictionary<string,string>();
            _kernel = new TestingKernel<ConsoleLog>(mockConfig: _configDic);
            _kernel.Rebind(typeof(CachingProxyProvider<>))
                .ToSelf()      
                .InTransientScope();
            var fakeRevokingManager =new FakeRevokingManager();
            _kernel.Rebind<IRevokeListener>().ToConstant(fakeRevokingManager);
            _kernel.Rebind<ICacheRevoker>().ToConstant(fakeRevokingManager);

        }

        [SetUp]
        public void Setup()
        {            
            SetupServiceMock();
            SetupDateTime();
            _revokeDelay =Task.Delay(0);
            
            _proxy = _kernel.Get<ICachingTestService>();
            _cacheRevoker = _kernel.Get<ICacheRevoker>();
            _revokeListener = _kernel.Get<IRevokeListener>();
        }

        [TearDown]
        public void TearDown()
        {
            _kernel.Get<AsyncCache>().Clear();
        }

        private void SetupServiceMock()
        {             
            _serviceMock = Substitute.For<ICachingTestService>();
            _serviceMock.CallService().Returns(_ =>
            {
                return Task.FromResult(_serviceResult);
            });
            _serviceMock.CallRevocableService(Arg.Any<string>()).Returns(async s =>
            {
                var result = _serviceResult;
                await _revokeDelay;
                return new Revocable<string>
                {
                    Value = result,
                    RevokeKeys = new[] {s.Args()[0].ToString()}
                };
            });
        
            _serviceResult = FirstResult;
            var serviceProxyMock = Substitute.For<IServiceProxyProvider<ICachingTestService>>();
            serviceProxyMock.Client.Returns(_serviceMock);
            _kernel.Rebind<IServiceProxyProvider<ICachingTestService>>().ToConstant(serviceProxyMock);
         
        }

        private void SetupDateTime()
        {
            _now = DateTime.UtcNow;
            var dateTimeMock = Substitute.For<IDateTime>();
            dateTimeMock.UtcNow.Returns(_=>_now);
            _kernel.Rebind<IDateTime>().ToConstant(dateTimeMock);
        }

        [Test]
        public async Task CachingEnabledByDefault()
        {
            await ClearCachingPolicyConfig();
            await ServiceResultShouldBeCached();
        }

        [Test]
        public async Task CachingDisabledByConfiguration()
        {            
            await SetCachingPolicyConfig(new[] {"Enabled", "false"});
            await ServiceResultShouldNotBeCached();
        }

        [Test]
        public async Task CallServiceWithoutReturnValueSucceed() //bug #139872
        {
            await _proxy.CallServiceWithoutReturnValue();
        }

        [Test]
        public async Task CachingDisabledByMethodConfiguration()
        {
            await SetCachingPolicyConfig(new[] { "Methods.CallService.Enabled", "false" });
            await ServiceResultShouldNotBeCached();
        }

        [Test]
        public async Task CachingOfOtherMathodDisabledByConfiguration()
        {
            await SetCachingPolicyConfig(new[] { "Methods.OtherMethod.Enabled", "false" });
            await ServiceResultShouldBeCached();
        }

        [Test]
        public async Task CachingRefreshTimeByConfiguration()
        {
            TimeSpan expectedRefreshTime = TimeSpan.FromSeconds(10);
            await SetCachingPolicyConfig(new [] { "RefreshTime", expectedRefreshTime.ToString()});
            await ServiceResultShouldRefreshOnBackgroundAfter(expectedRefreshTime);
        }

        [Test]
        public async Task CallWhileRefreshShouldReturnOldValueAndAfterRefreshTheNewValue()
        {
            TimeSpan expectedRefreshTime = TimeSpan.FromSeconds(10);
            await SetCachingPolicyConfig(new[] { "RefreshTime", expectedRefreshTime.ToString() });

            var result = await _proxy.CallService();
            result.ShouldBe(FirstResult); 

            _now += expectedRefreshTime;
            _serviceResult = SecondResult;
            
            result = await _proxy.CallService(); //trigger a refresh, but until it will finish, return old result
            result.ShouldBe(FirstResult);

            await Task.Delay(100); //wait for refresh to end

            result = await _proxy.CallService();
            result.ShouldBe(SecondResult); //refreshed value should be returned
        }

        [Test]
        public async Task DoNotExtendExpirationWhenReadFromCache_CallAfterCacheItemIsExpiredShouldTriggerACallToTheService()
        {
            TimeSpan expectedExpirationTime = TimeSpan.FromSeconds(1);
            await SetCachingPolicyConfig(new[] { "ExpirationTime",     expectedExpirationTime.ToString() }, 
                                         new[] { "ExpirationBehavior", "DoNotExtendExpirationWhenReadFromCache" });

            //First call to service - value is cached
            var result = await _proxy.CallService();
            result.ShouldBe(FirstResult);

            _serviceResult = SecondResult;

            //No service call - cached value is used
            result = await _proxy.CallService();
            result.ShouldBe(FirstResult);

            //Wait for item to be expired
            await Task.Delay(1500);

            //Prev item is expired - make a call to the service
            result = await _proxy.CallService(); 
            result.ShouldBe(SecondResult);
        }

        [Test]
        [Retry(3)] //Sometimes fails in build server because of timing issues
        public async Task ExtendExpirationWhenReadFromCache_CallAfterCacheItemIsExpiredAndExtendedShouldNotTriggerACallToTheService()
        {
            TimeSpan expectedExpirationTime = TimeSpan.FromSeconds(3);
            await SetCachingPolicyConfig(new[] { "ExpirationTime",     expectedExpirationTime.ToString() },
                                         new[] { "ExpirationBehavior", "ExtendExpirationWhenReadFromCache" });

            //First call to service - value is cached
            var result = await _proxy.CallService();
            result.ShouldBe(FirstResult);

            _serviceResult = SecondResult;

            //Time has passed, but expiration has not reached
            await Task.Delay(1000);

            //No service call - cached value is used and expiration is extended
            result = await _proxy.CallService();
            result.ShouldBe(FirstResult);

            //Additional time has passed (beyond the expectedExpirationTime)
            await Task.Delay(2100);

            //Prev item is not expired (expiration was extended) - no service call
            result = await _proxy.CallService();
            result.ShouldBe(FirstResult);
        }

        [Test]
        public async Task CachingRefreshTimeByMethodConfiguration()
        {
            TimeSpan expectedRefreshTime = TimeSpan.FromSeconds(10);
            await SetCachingPolicyConfig(new[] { "Methods.CallService.RefreshTime", expectedRefreshTime.ToString() });
            await ServiceResultShouldRefreshOnBackgroundAfter(expectedRefreshTime);
        }

        [Test]
        public async Task CachedDataShouldBeRevoked()
        {
            var key = Guid.NewGuid().ToString();
            await ClearCachingPolicyConfig();

            await ResultlRevocableServiceShouldBe(FirstResult, key);
            _serviceResult = SecondResult;
            await ResultlRevocableServiceShouldBe(FirstResult, key, "Result should have been cached");
            await _cacheRevoker.Revoke(key);
            _revokeListener.RevokeSource.WhenEventReceived(TimeSpan.FromMinutes(1));
            await Task.Delay(100);
            await ResultlRevocableServiceShouldBe(SecondResult, key, "Result shouldn't have been cached");
        }

        [Test]
        public async Task RevokeBeforeServiceResultReceivedShouldRevokeStaleValue()
        {
            var key = Guid.NewGuid().ToString();
            await ClearCachingPolicyConfig();
            var delay = new TaskCompletionSource<int>();
            _revokeDelay = delay.Task;
            var serviceCallWillCompleteOnlyAfterRevoke = ResultlRevocableServiceShouldBe(FirstResult, key, "Result should have been cached");
            await _cacheRevoker.Revoke(key);
            _revokeListener.RevokeSource.WhenEventReceived(TimeSpan.FromMinutes(1));
            delay.SetResult(1);
            await serviceCallWillCompleteOnlyAfterRevoke;
            await Task.Delay(100);
            _serviceResult = SecondResult;
            await ResultlRevocableServiceShouldBe(SecondResult, key, "Result shouldn't have been cached");
        }

        private async Task SetCachingPolicyConfig(params string[][] keyValues)
        {
            var changed = false;

            if (_configDic.Values.Count != 0 && keyValues.Length==0)
                changed = true;

            _configDic.Clear();
            foreach (var keyValue in keyValues)
            {
                var key = keyValue[0];
                var value = keyValue[1];
                if (key != null && value != null)
                {
                    _kernel.Get<OverridableConfigItems>()
                        .SetValue($"Discovery.Services.CachingTestService.CachingPolicy.{key}", value);
                    changed = true;
                }
            }
            if (changed)
            {
                await _kernel.Get<ManualConfigurationEvents>().ApplyChanges<DiscoveryConfig>();
                await Task.Delay(200);
            }
        }

        private async Task ClearCachingPolicyConfig()
        {
            await SetCachingPolicyConfig();
        }

        private async Task ServiceResultShouldBeCached()
        {
            await ResultShouldBe(FirstResult);
            _serviceResult = SecondResult;
            await ResultShouldBe(FirstResult, "Result should have been cached");
        }

        private async Task ServiceResultShouldNotBeCached()
        {
            await ResultShouldBe(FirstResult);
            _serviceResult = SecondResult;
            await ResultShouldBe(SecondResult, "Result shouldn't have been cached");
        }

        private async Task ServiceResultShouldRefreshOnBackgroundAfter(TimeSpan timeSpan)
        {
            await ResultShouldBe(FirstResult);
            _serviceResult = SecondResult;
            _now += timeSpan;
            await TriggerCacheRefreshOnBackground();   
            await ResultShouldBe(SecondResult, $"Cached value should have been background-refreshed after {timeSpan}");
        }

        private async Task TriggerCacheRefreshOnBackground()
        {
            await _proxy.CallService();
        }

        private async Task ResultShouldBe(string expectedResult, string message = null)
        {
            var result = await _proxy.CallService();
            result.ShouldBe(expectedResult, message);
        }

        private async Task ResultlRevocableServiceShouldBe(string expectedResult,string key ,string message = null)
        {
            var result = await _proxy.CallRevocableService(key);
            result.Value.ShouldBe(expectedResult, message);
        }
    }

    [HttpService(1234)]
    public interface ICachingTestService
    {
        [Gigya.Common.Contracts.Attributes.Cached]
        Task<string> CallService();

        [Gigya.Common.Contracts.Attributes.Cached]
        Task<string> OtherMethod();

        [Gigya.Common.Contracts.Attributes.Cached]
        Task<Revocable<string>> CallRevocableService(string keyToRevock);

        Task CallServiceWithoutReturnValue();
    }

    public class FakeRevokingManager : ICacheRevoker, IRevokeListener
    {
        private readonly BroadcastBlock<string> _broadcastBlock = new BroadcastBlock<string>(null);
        public Task Revoke(string key)
        {
            return _broadcastBlock.SendAsync(key);
        }

        public ISourceBlock<string> RevokeSource => _broadcastBlock;
    }

}
