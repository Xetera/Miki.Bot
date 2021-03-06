﻿using System;
using System.Threading.Tasks;

namespace Miki.Accounts.Achievements.Objects
{
	internal class UserUpdateAchievement : BaseAchievement
	{
		public Func<UserUpdatePacket, Task<bool>> CheckUserUpdate;

		public override async Task<bool> CheckAsync(BasePacket packet)
		{
			return await CheckUserUpdate(packet as UserUpdatePacket);
		}
	}
}