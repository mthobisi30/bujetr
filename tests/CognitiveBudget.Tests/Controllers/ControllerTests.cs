using CognitiveBudget.Web.Controllers;
using CognitiveBudget.Web.Models.Domain;
using CognitiveBudget.Web.Services;
using CognitiveBudget.Web.Data.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Moq;
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Xunit;

namespace CognitiveBudget.Tests.Controllers
{
    public class CommitmentDevicesControllerTests
    {
        private readonly Mock<UserManager<ApplicationUser>> _userManagerMock;
        private readonly Mock<ICommitmentDeviceService> _serviceMock;
        private readonly CommitmentDevicesController _sut;

        public CommitmentDevicesControllerTests()
        {
            var store = new Mock<IUserStore<ApplicationUser>>();
            _userManagerMock = new Mock<UserManager<ApplicationUser>>(store.Object, null, null, null, null, null, null, null, null);
            _serviceMock = new Mock<ICommitmentDeviceService>();
            _sut = new CommitmentDevicesController(_userManagerMock.Object, _serviceMock.Object);
        }

        private void SetupUser(string id)
        {
            var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", id) }, "test"));
            _sut.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
            _userManagerMock.Setup(m => m.GetUserId(It.IsAny<ClaimsPrincipal>())).Returns(id);
        }

        [Fact]
        public async Task Index_ReturnsViewWithDevices()
        {
            // Arrange
            SetupUser("user1");
            _serviceMock.Setup(s => s.GetUserDevicesAsync("user1"))
                        .ReturnsAsync(new List<CommitmentDevice> { new() { Name = "x" } });

            // Act
            var result = await _sut.Index() as ViewResult;

            // Assert
            Assert.NotNull(result);
            var model = Assert.IsAssignableFrom<IEnumerable<CommitmentDevice>>(result.Model);
            Assert.Single(model);
        }
    }

    public class SpendingTriggersControllerTests
    {
        private readonly Mock<UserManager<ApplicationUser>> _userManagerMock;
        private readonly Mock<ISpendingTriggerRepository> _repoMock;
        private readonly SpendingTriggersController _sut;

        public SpendingTriggersControllerTests()
        {
            var store = new Mock<IUserStore<ApplicationUser>>();
            _userManagerMock = new Mock<UserManager<ApplicationUser>>(store.Object, null, null, null, null, null, null, null, null);
            _repoMock = new Mock<ISpendingTriggerRepository>();
            _sut = new SpendingTriggersController(_userManagerMock.Object, _repoMock.Object);
        }

        private void SetupUser(string id)
        {
            var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", id) }, "test"));
            _sut.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
            _userManagerMock.Setup(m => m.GetUserId(It.IsAny<ClaimsPrincipal>())).Returns(id);
        }

        [Fact]
        public async Task Index_ReturnsTriggers()
        {
            SetupUser("u2");
            _repoMock.Setup(r => r.GetAllByUserIdAsync("u2"))
                     .ReturnsAsync(new List<SpendingTrigger> { new() { Label = "foo", Insight = "bar" } });
            var result = await _sut.Index() as ViewResult;
            Assert.NotNull(result);
            var model = Assert.IsAssignableFrom<IEnumerable<SpendingTrigger>>(result.Model);
            Assert.Single(model);
        }
    }
}
