﻿using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
namespace YooAsset
{
	public  abstract class ProviderBase
	{
		public enum EStatus
		{
			None = 0,
			CheckBundle,
			Loading,
			Checking,
			Success,
			Fail,
		}

		/// <summary>
		/// 资源路径
		/// </summary>
		public string AssetPath { private set; get; }

		/// <summary>
		/// 资源对象的名称
		/// </summary>
		public string AssetName { private set; get; }

		/// <summary>
		/// 资源对象的类型
		/// </summary>
		public System.Type AssetType { private set; get; }

		/// <summary>
		/// 获取的资源对象
		/// </summary>
		public UnityEngine.Object AssetObject { protected set; get; }

		/// <summary>
		/// 获取的资源对象集合
		/// </summary>
		public UnityEngine.Object[] AllAssetObjects { protected set; get; }

		/// <summary>
		/// 获取的场景对象
		/// </summary>
		public UnityEngine.SceneManagement.Scene SceneObject { protected set; get; }


		/// <summary>
		/// 当前的加载状态
		/// </summary>
		public EStatus Status { protected set; get; } = EStatus.None;

		/// <summary>
		/// 引用计数
		/// </summary>
		public int RefCount { private set; get; } = 0;

		/// <summary>
		/// 是否已经销毁
		/// </summary>
		public bool IsDestroyed { private set; get; } = false;

		/// <summary>
		/// 是否完毕（成功或失败）
		/// </summary>
		public bool IsDone
		{
			get
			{
				return Status == EStatus.Success || Status == EStatus.Fail;
			}
		}

		/// <summary>
		/// 加载进度
		/// </summary>
		public virtual float Progress
		{
			get
			{
				return 0;
			}
		}


		protected bool IsWaitForAsyncComplete { private set; get; } = false;
		private readonly List<OperationHandleBase> _handles = new List<OperationHandleBase>();


		public ProviderBase(string assetPath, System.Type assetType)
		{
			AssetPath = assetPath;
			AssetName = assetPath;
			AssetType = assetType;
		}

		/// <summary>
		/// 轮询更新方法
		/// </summary>
		public abstract void Update();

		/// <summary>
		/// 销毁资源对象
		/// </summary>
		public virtual void Destory()
		{
			IsDestroyed = true;
		}

		/// <summary>
		/// 是否可以销毁
		/// </summary>
		public bool CanDestroy()
		{
			if (IsDone == false)
				return false;

			return RefCount <= 0;
		}

		/// <summary>
		/// 创建操作句柄
		/// </summary>
		/// <returns></returns>
		public OperationHandleBase CreateHandle()
		{
			// 引用计数增加
			RefCount++;

			OperationHandleBase handle;
			if (IsSceneProvider())
				handle = new SceneOperationHandle(this);
			else if (IsSubAssetsProvider())
				handle = new SubAssetsOperationHandle(this);
			else
				handle = new AssetOperationHandle(this);

			_handles.Add(handle);
			return handle;
		}

		/// <summary>
		/// 释放操作句柄
		/// </summary>
		public void ReleaseHandle(OperationHandleBase handle)
		{
			if (RefCount <= 0)
				Logger.Warning("Asset provider reference count is already zero. There may be resource leaks !");

			if (_handles.Remove(handle) == false)
				throw new System.Exception("Should never get here !");

			// 引用计数减少
			RefCount--;
		}

		/// <summary>
		/// 是否为场景提供者
		/// </summary>
		public bool IsSceneProvider()
		{
			if (this is BundledSceneProvider || this is DatabaseSceneProvider)
				return true;
			else
				return false;
		}

		/// <summary>
		/// 是否为子资源对象提供者
		/// </summary>
		public bool IsSubAssetsProvider()
		{
			if (this is BundledSubAssetsProvider || this is DatabaseSubAssetsProvider)
				return true;
			else
				return false;
		}

		/// <summary>
		/// 等待异步执行完毕
		/// </summary>
		public void WaitForAsyncComplete()
		{
			IsWaitForAsyncComplete = true;

			// 注意：主动轮询更新完成同步加载
			Update();

			// 验证结果
			if (IsDone == false)
			{
				Logger.Warning($"WaitForAsyncComplete failed to loading : {AssetPath}");
			}
		}

		/// <summary>
		/// 异步操作任务
		/// </summary>
		public System.Threading.Tasks.Task<object> Task
		{
			get
			{
				var handle = WaitHandle;
				return System.Threading.Tasks.Task.Factory.StartNew(o =>
				{
					handle.WaitOne();
					return AssetObject as object;
				}, this);
			}
		}

		#region 异步编程相关
		private System.Threading.EventWaitHandle _waitHandle;
		private System.Threading.WaitHandle WaitHandle
		{
			get
			{
				if (_waitHandle == null)
					_waitHandle = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.ManualReset);
				_waitHandle.Reset();
				return _waitHandle;
			}
		}
		protected void InvokeCompletion()
		{
            List<OperationHandleBase> tempers = new List<OperationHandleBase>(_handles);
            foreach (var hande in tempers)
            {
                if (hande.IsValid)
                {
                    hande.InvokeCallback();
                }
            }
            _waitHandle?.Set();
		}
        #endregion
        #region 调试信息相关
        /// <summary>
        /// 出生的场景
        /// </summary>
        public string SpawnScene = string.Empty;

        /// <summary>
        /// 出生的时间
        /// </summary>
        public string SpawnTime = string.Empty;

        [Conditional("DEBUG")]
        public void InitSpawnDebugInfo()
        {
            SpawnScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; ;
            SpawnTime = SpawnTimeToString(UnityEngine.Time.realtimeSinceStartup);
        }
        private string SpawnTimeToString(float spawnTime)
        {
            float h = UnityEngine.Mathf.FloorToInt(spawnTime / 3600f);
            float m = UnityEngine.Mathf.FloorToInt(spawnTime / 60f - h * 60f);
            float s = UnityEngine.Mathf.FloorToInt(spawnTime - m * 60f - h * 3600f);
            return h.ToString("00") + ":" + m.ToString("00") + ":" + s.ToString("00");
        }
        #endregion
    }
}