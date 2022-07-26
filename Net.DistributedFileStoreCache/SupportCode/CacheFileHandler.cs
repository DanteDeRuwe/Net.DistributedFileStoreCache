﻿// Copyright (c) 2022 Jon P Smith, GitHub: JonPSmith, web: http://www.thereformedprogrammer.net/
// Licensed under MIT license. See License.txt in the project root for license information.

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace Net.DistributedFileStoreCache.SupportCode;

internal class CacheFileHandler
{
    private readonly DistributedFileStoreCacheOptions _options;

    public CacheFileHandler (DistributedFileStoreCacheOptions options)
    {
        _options = options;
    }

    public string? GetValue(string key)
    {
        if (StaticCachePart.LocalCacheIsOutOfDate)
            _options.TryAgainOnUnauthorizedAccess(UpdateLocalCacheFromCacheFile);

        return StaticCachePart.CacheContent.ReturnNullIfExpires(key);
    }

    public async Task<string?> GetValueAsync(string key, CancellationToken token)
    {
        if (StaticCachePart.LocalCacheIsOutOfDate)
            await _options.TryAgainOnUnauthorizedAccessAsync(() => UpdateLocalCacheFromCacheFileAsync(token));

        return StaticCachePart.CacheContent.ReturnNullIfExpires(key);
    }

    public IReadOnlyDictionary<string, string> GetAllValues()
    {
        if (StaticCachePart.LocalCacheIsOutOfDate)
            _options.TryAgainOnUnauthorizedAccess(UpdateLocalCacheFromCacheFile);

        return StaticCachePart.CacheContent.ReturnNonExpiredCacheValues();
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllValuesAsync(CancellationToken token)
    {
        if (StaticCachePart.LocalCacheIsOutOfDate)
            await _options.TryAgainOnUnauthorizedAccessAsync(() => UpdateLocalCacheFromCacheFileAsync(token));

        return StaticCachePart.CacheContent.ReturnNonExpiredCacheValues();
    }

    public void SetKeyValue(string key, string value, DistributedCacheEntryOptions? entryOptions)
    {
        _options.TryAgainOnUnauthorizedAccess(() =>
            ReadAndChangeCacheJsonFile(CacheChanges.Add, false, key, value, entryOptions)
                .CheckSyncValueTaskWorked());
    }

    public async Task SetKeyValueAsync(string key, string value, DistributedCacheEntryOptions? entryOptions,
        CancellationToken token)
    {
        await _options.TryAgainOnUnauthorizedAccessAsync(async () =>
            await ReadAndChangeCacheJsonFile(CacheChanges.Add, true, key, value, entryOptions, token));
    }

    public void RemoveKeyValue(string key)
    {
        _options.TryAgainOnUnauthorizedAccess(() =>
            ReadAndChangeCacheJsonFile(CacheChanges.Remove, false, key)
                .CheckSyncValueTaskWorked());
    }

    public async Task RemoveKeyValueAsync(string key, CancellationToken token)
    {
        await _options.TryAgainOnUnauthorizedAccessAsync(async () =>
            await ReadAndChangeCacheJsonFile(CacheChanges.Remove, false, key, token: token));
    }

    public void ResetCacheFile()
    {
        _options.TryAgainOnUnauthorizedAccess(() =>
            ReadAndChangeCacheJsonFile(CacheChanges.Reset, false).CheckSyncValueTaskWorked());
    }


    /// <summary>
    /// This should ONLY be used on startup. Its job is to ensure there is a cache file 
    /// </summary>
    public void CreateNewCacheFileIfMissingWithRetry()
    {
        //Create a valid cache file containing no key/values
        var writeBytes = FillByteBufferWithCacheJsonData(new CacheJsonContent());

        //We run this within a retry loop to make sure it succeeds 
        _options.TryAgainOnUnauthorizedAccess(() =>
        {
            var cacheFilePath = _options.FormCacheFilePath();
            if (!File.Exists(cacheFilePath))
            {
                //This uses FileMode.CreateNew to ensure only one file is created
                using FileStream writeStream = new FileStream(cacheFilePath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, bufferSize: 1, false);
                {
                    writeStream.Write(writeBytes, 0, writeBytes.Length);
                }
            }
        });
    }

    //-----------------------------------------------------------------
    //private methods

    private void UpdateLocalCacheFromCacheFile()
    {
        var readBuffer = new byte[_options.MaxBytesInJsonCacheFile];
        var readFilePath = _options.FormCacheFilePath();

        //This uses FileShare.None to ensure multiple instances don't try to update the in-memory cache at the same time
        using FileStream readStream = new FileStream(readFilePath, FileMode.Open, FileAccess.Read, FileShare.None,
            bufferSize: 1, false);
        {
            var numBytesRead = readStream.Read(readBuffer);
            if (numBytesRead >= _options.MaxBytesInJsonCacheFile)
                throw new DistributedFileStoreCacheException(
                    $"Your cache json file has more that {_options.MaxBytesInJsonCacheFile} " +
                    $"bytes, so you MUST set the option's {nameof(DistributedFileStoreCacheOptions.MaxBytesInJsonCacheFile)} to a bigger value.");

            StaticCachePart.UpdateLocalCache(GetJsonFromByteBuffer(numBytesRead, ref readBuffer));
        }
    }

    private async ValueTask UpdateLocalCacheFromCacheFileAsync(CancellationToken token)
    {
        var readBuffer = new byte[_options.MaxBytesInJsonCacheFile];
        var readFilePath = _options.FormCacheFilePath();
        //This uses FileShare.None to ensure multiple instances don't try to update the in-memory cache at the same time
        using FileStream readStream = new FileStream(readFilePath, FileMode.Open, FileAccess.Read, FileShare.None,
            bufferSize: 1, true);
        {
            var numBytesRead = await readStream.ReadAsync(readBuffer, token);
            if (numBytesRead >= _options.MaxBytesInJsonCacheFile)
                throw new DistributedFileStoreCacheException(
                    $"Your cache json file has more that {_options.MaxBytesInJsonCacheFile} " +
                    $"bytes, so you MUST set the option's {nameof(DistributedFileStoreCacheOptions.MaxBytesInJsonCacheFile)} to a bigger value.");

            StaticCachePart.UpdateLocalCache(GetJsonFromByteBuffer(numBytesRead, ref readBuffer));
        }
    }

    private enum CacheChanges { Add, Remove, Reset }

    private async ValueTask ReadAndChangeCacheJsonFile(CacheChanges whatToDo, bool useAsync,
        string? key = null, string? value = null, DistributedCacheEntryOptions? entryOptions = null,
        CancellationToken token = new CancellationToken())
    {
        //thanks to https://stackoverflow.com/questions/15628902/lock-file-exclusively-then-delete-move-it for this approach

        int numBytesRead = 0;
        var readWriteBuffer = new byte[_options.MaxBytesInJsonCacheFile];
        var filePath = _options.FormCacheFilePath();
        using FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, useAsync);
        {
            if(whatToDo != CacheChanges.Reset)
            {
                numBytesRead = useAsync 
                    ? await fileStream.ReadAsync(readWriteBuffer, token)
                    : fileStream.Read(readWriteBuffer);
                if (numBytesRead >= _options.MaxBytesInJsonCacheFile)
                    throw new DistributedFileStoreCacheException(
                        $"Your cache json file has more that {_options.MaxBytesInJsonCacheFile} " +
                        $"bytes, so you MUST set the option's {nameof(DistributedFileStoreCacheOptions.MaxBytesInJsonCacheFile)} to a bigger value.");
            }

            CacheJsonContent json;
            switch (whatToDo)
            {
                case CacheChanges.Add:
                    if (key == null) throw new NullReferenceException("The key cannot be null");
                    if (value == null) throw new NullReferenceException("The value cannot be null");
                    json = GetJsonFromByteBuffer(numBytesRead, ref readWriteBuffer);
                    json.Cache[key] = value;
                    json.SetupTimeoutIfOptions(key, entryOptions);
                    break;
                case CacheChanges.Remove:
                    if (key == null) throw new NullReferenceException("The key cannot be null");
                    json = GetJsonFromByteBuffer(numBytesRead, ref readWriteBuffer);
                    json.Cache.Remove(key);
                    json.TimeOuts.Remove(key);
                    break;
                case CacheChanges.Reset:
                    json = new CacheJsonContent();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(whatToDo), whatToDo, null);
            }

            //We immediately update the local cache because a change of the expiration data isn't always updated to the file
            StaticCachePart.UpdateLocalCache(json);

            //thanks to https://stackoverflow.com/questions/15628902/lock-file-exclusively-then-delete-move-it
            fileStream.Seek(0, SeekOrigin.Begin);
            fileStream.SetLength(0);
            if (useAsync)
                await fileStream.WriteAsync(FillByteBufferWithCacheJsonData(json), token);
            else
                fileStream.Write(FillByteBufferWithCacheJsonData(json));


        }
    }

    private CacheJsonContent GetJsonFromByteBuffer(int numBytes, ref byte[] buffer)
    {
        if (numBytes == 0)
            return new CacheJsonContent();
        var jsonString = Encoding.UTF8.GetString(buffer, 0, numBytes);

        var cacheContent = JsonSerializer.Deserialize<CacheJsonContent>(jsonString)!;
        cacheContent.RemoveExpiredCacheValues();
        return cacheContent;
    }

    private byte[] FillByteBufferWithCacheJsonData(CacheJsonContent allCache)
    {
        var jsonString = JsonSerializer.Serialize(allCache,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });

        return Encoding.UTF8.GetBytes(jsonString);
    }
}