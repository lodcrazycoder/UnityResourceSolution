﻿using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using URS;
namespace YooAsset
{
	public class AssetBundleLoader
	{
		public enum EStatus
		{
			None = 0,
			Download,
			CheckDownload,
			LoadFile,
			CheckFile,
			Success,
			Fail,
		}

		/// <summary>
		/// 资源包文件信息
		/// </summary>
		public HardiskFileSearchResult HardiskFileSearchResult { private set; get; }

		/// <summary>
		/// 引用计数
		/// </summary>
		public int RefCount { private set; get; }

		/// <summary>
		/// 加载状态
		/// </summary>
		public EStatus Status { private set; get; }

		/// <summary>
		/// 是否已经销毁
		/// </summary>
		public bool IsDestroyed { private set; get; } = false;

		private readonly List<ProviderBase> _providers = new List<ProviderBase>(100);
		private bool _isWaitForAsyncComplete = false;
		private bool _isShowWaitForAsyncError = false;
		private RemoteDownloader _fileDownloader;
		private AssetBundleCreateRequest _cacheRequest;
		internal AssetBundle CacheBundle { private set; get; }


		public AssetBundleLoader(HardiskFileSearchResult hardiskFileSearchResult)
		{
            HardiskFileSearchResult = hardiskFileSearchResult;
			RefCount = 0;
			Status = EStatus.None;
		}

		/// <summary>
		/// 添加附属的资源提供者
		/// </summary>
		public void AddProvider(ProviderBase provider)
		{
			if (_providers.Contains(provider) == false)
				_providers.Add(provider);
		}

		/// <summary>
		/// 引用（引用计数递加）
		/// </summary>
		public void Reference()
		{
			RefCount++;
		}

		/// <summary>
		/// 释放（引用计数递减）
		/// </summary>
		public void Release()
		{
			RefCount--;
		}

		/// <summary>
		/// 轮询更新
		/// </summary>
		public void Update()
		{
			// 如果资源文件加载完毕
			if (IsDone())
				return;

			if (Status == EStatus.None)
			{
				// 检测加载地址是否为空
				if (!HardiskFileSearchResult.IsValidFile())
				{
					Status = EStatus.Fail;
					return;
				}

                if (HardiskFileSearchResult.IsLocalFileExist())
					Status = EStatus.LoadFile;
				else
					Status = EStatus.Download;
			}

			// 1. 从服务器下载
			if (Status == EStatus.Download)
			{
				int failedTryAgain = int.MaxValue;
				_fileDownloader = RemoteDownloadSystem.BeginDownload(HardiskFileSearchResult.UpdateEntry, failedTryAgain);
				Status = EStatus.CheckDownload;
			}

			// 2. 检测服务器下载结果
			if (Status == EStatus.CheckDownload)
			{
				if (_fileDownloader.IsDone() == false)
					return;

				if (_fileDownloader.HasError())
				{
					_fileDownloader.ReportError();
					Status = EStatus.Fail;
				}
				else
				{
					Status = EStatus.LoadFile;
				}
			}

			// 3. 加载AssetBundle
			if (Status == EStatus.LoadFile)
			{
#if UNITY_EDITOR
				// 注意：Unity2017.4编辑器模式下，如果AssetBundle文件不存在会导致编辑器崩溃，这里做了预判。
				if (System.IO.File.Exists(HardiskFileSearchResult.HardiskPath) == false)
				{
					Logger.Warning($"Not found assetBundle file : {HardiskFileSearchResult.HardiskPath}");
					Status = EStatus.Fail;
					return;
				}
#endif

				// Load assetBundle file
				if (HardiskFileSearchResult.IsEncrypted)
				{
					if (AssetSystem.DecryptionServices == null)
						throw new Exception($"{nameof(AssetBundleLoader)} need IDecryptServices : {HardiskFileSearchResult.OrignRelativePath}");

					ulong offset = AssetSystem.DecryptionServices.GetDecryptOffset(HardiskFileSearchResult.FileMeta);
					if (_isWaitForAsyncComplete)
						CacheBundle = AssetBundle.LoadFromFile(HardiskFileSearchResult.HardiskPath, 0, offset);
					else
						_cacheRequest = AssetBundle.LoadFromFileAsync(HardiskFileSearchResult.HardiskPath, 0, offset);
				}
				else
				{
					if (_isWaitForAsyncComplete)
						CacheBundle = AssetBundle.LoadFromFile(HardiskFileSearchResult.HardiskPath);
					else
						_cacheRequest = AssetBundle.LoadFromFileAsync(HardiskFileSearchResult.HardiskPath);
				}
				Status = EStatus.CheckFile;
			}

			// 4. 检测AssetBundle加载结果
			if (Status == EStatus.CheckFile)
			{
				if (_cacheRequest != null)
				{
					if (_isWaitForAsyncComplete)
					{
						// 强制挂起主线程（注意：该操作会很耗时）
						Logger.Warning("Suspend the main thread to load unity bundle.");
						CacheBundle = _cacheRequest.assetBundle;
					}
					else
					{
						if (_cacheRequest.isDone == false)
							return;
						CacheBundle = _cacheRequest.assetBundle;
					}
				}

				// Check error
				if (CacheBundle == null)
				{
					Logger.Error($"Failed to load assetBundle file : {HardiskFileSearchResult.OrignRelativePath}");
					Status = EStatus.Fail;
				}
				else
				{
					Status = EStatus.Success;
				}
			}
		}

		/// <summary>
		/// 销毁
		/// </summary>
		public void Destroy(bool forceDestroy)
		{
			IsDestroyed = true;

			// Check fatal
			if (forceDestroy == false)
			{
				if (RefCount > 0)
					throw new Exception($"Bundle file loader ref is not zero : {HardiskFileSearchResult.OrignRelativePath}");
				if (IsDone() == false)
					throw new Exception($"Bundle file loader is not done : {HardiskFileSearchResult.OrignRelativePath}");
			}

			if (CacheBundle != null)
			{
				CacheBundle.Unload(true);
				CacheBundle = null;
			}
		}

		/// <summary>
		/// 是否完毕（无论成功或失败）
		/// </summary>
		public bool IsDone()
		{
			return Status == EStatus.Success || Status == EStatus.Fail;
		}

		/// <summary>
		/// 是否可以销毁
		/// </summary>
		public bool CanDestroy()
		{
			if (IsDone() == false)
				return false;

			return RefCount <= 0;
		}

		/// <summary>
		/// 在满足条件的前提下，销毁所有资源提供者
		/// </summary>
		public void TryDestroyAllProviders()
		{
			if (IsDone() == false)
				return;

			// 注意：必须等待所有Provider可以销毁的时候，才可以释放Bundle文件。
			foreach (var provider in _providers)
			{
				if (provider.CanDestroy() == false)
					return;
			}

			// 除了自己没有其它引用
			if (RefCount > _providers.Count)
				return;

			// 销毁所有Providers
			foreach (var provider in _providers)
			{
				provider.Destory();
			}

			// 从列表里移除Providers
			AssetSystem.RemoveBundleProviders(_providers);
			_providers.Clear();
		}

		/// <summary>
		/// 主线程等待异步操作完毕
		/// </summary>
		public void WaitForAsyncComplete()
		{
			_isWaitForAsyncComplete = true;

			int frame = 1000;
			while (true)
			{
				// 保险机制
				// 注意：如果需要从WEB端下载资源，可能会触发保险机制！
				frame--;
				if (frame == 0)
				{
					if (_isShowWaitForAsyncError == false)
					{
						_isShowWaitForAsyncError = true;
						Logger.Error($"WaitForAsyncComplete failed ! BundleName : {HardiskFileSearchResult.OrignRelativePath} States : {Status}");
					}
					break;
				}

				// 驱动流程
				Update();

				// 完成后退出
				if (IsDone())
					break;
			}
		}
	}
}