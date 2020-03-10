﻿using LibHac.Common;
using LibHac.Fs;
using Xunit;

namespace LibHac.Tests.Fs.IFileSystemTestBase
{
    public abstract class IAttributeFileSystemTests : IFileSystemTests
    {
        protected abstract IAttributeFileSystem CreateAttributeFileSystem();

        [Fact]
        public void CreateDirectory_WithoutArchiveAttribute_ArchiveFlagIsNotSet()
        {
            IAttributeFileSystem fs = CreateAttributeFileSystem();

            Assert.True(fs.CreateDirectory("/dir".ToU8Span(), NxFileAttributes.None).IsSuccess());

            Assert.True(fs.GetFileAttributes(out NxFileAttributes attributes, "/dir".ToU8Span()).IsSuccess());
            Assert.Equal(NxFileAttributes.Directory, attributes);
        }

        [Fact]
        public void CreateDirectory_WithArchiveAttribute_ArchiveFlagIsSet()
        {
            IAttributeFileSystem fs = CreateAttributeFileSystem();

            Assert.True(fs.CreateDirectory("/dir".ToU8Span(), NxFileAttributes.Archive).IsSuccess());

            Assert.True(fs.GetFileAttributes(out NxFileAttributes attributes, "/dir".ToU8Span()).IsSuccess());
            Assert.Equal(NxFileAttributes.Directory | NxFileAttributes.Archive, attributes);
        }

        [Fact]
        public void GetFileAttributes_AttributesOnNewFileAreEmpty()
        {
            IAttributeFileSystem fs = CreateAttributeFileSystem();
            fs.CreateFile("/file".ToU8Span(), 0, CreateFileOptions.None);

            Result rc = fs.GetFileAttributes(out NxFileAttributes attributes, "/file".ToU8Span());

            Assert.True(rc.IsSuccess());
            Assert.Equal(NxFileAttributes.None, attributes);
        }

        [Fact]
        public void GetFileAttributes_AttributesOnNewDirHaveOnlyDirFlagSet()
        {
            IAttributeFileSystem fs = CreateAttributeFileSystem();
            fs.CreateDirectory("/dir".ToU8Span());

            Result rc = fs.GetFileAttributes(out NxFileAttributes attributes, "/dir".ToU8Span());

            Assert.True(rc.IsSuccess());
            Assert.Equal(NxFileAttributes.Directory, attributes);
        }

        [Fact]
        public void GetFileAttributes_PathDoesNotExist_ReturnsPathNotFound()
        {
            IAttributeFileSystem fs = CreateAttributeFileSystem();

            Result rc = fs.GetFileAttributes(out _, "/path".ToU8Span());

            Assert.Equal(ResultFs.PathNotFound.Value, rc);
        }

        [Fact]
        public void SetFileAttributes_PathDoesNotExist_ReturnsPathNotFound()
        {
            IAttributeFileSystem fs = CreateAttributeFileSystem();

            Result rc = fs.SetFileAttributes("/path".ToU8Span(), NxFileAttributes.None);

            Assert.Equal(ResultFs.PathNotFound.Value, rc);
        }

        [Fact]
        public void SetFileAttributes_SetAttributeOnFile()
        {
            IAttributeFileSystem fs = CreateAttributeFileSystem();
            fs.CreateFile("/file".ToU8Span(), 0, CreateFileOptions.None);

            Result rcSet = fs.SetFileAttributes("/file".ToU8Span(), NxFileAttributes.Archive);
            Result rcGet = fs.GetFileAttributes(out NxFileAttributes attributes, "/file".ToU8Span());

            Assert.True(rcSet.IsSuccess());
            Assert.True(rcGet.IsSuccess());
            Assert.Equal(NxFileAttributes.Archive, attributes);
        }

        [Fact]
        public void SetFileAttributes_SetAttributeOnDirectory()
        {
            IAttributeFileSystem fs = CreateAttributeFileSystem();
            fs.CreateDirectory("/dir".ToU8Span());

            Result rcSet = fs.SetFileAttributes("/dir".ToU8Span(), NxFileAttributes.Archive);
            Result rcGet = fs.GetFileAttributes(out NxFileAttributes attributes, "/dir".ToU8Span());

            Assert.True(rcSet.IsSuccess());
            Assert.True(rcGet.IsSuccess());
            Assert.Equal(NxFileAttributes.Directory | NxFileAttributes.Archive, attributes);
        }

        [Fact]
        public void GetFileSize_ReadNewFileSize()
        {
            IAttributeFileSystem fs = CreateAttributeFileSystem();

            fs.CreateFile("/file".ToU8Span(), 845, CreateFileOptions.None);

            Assert.True(fs.GetFileSize(out long fileSize, "/file".ToU8Span()).IsSuccess());
            Assert.Equal(845, fileSize);
        }

        [Fact]
        public void GetFileSize_PathDoesNotExist_ReturnsPathNotFound()
        {
            IAttributeFileSystem fs = CreateAttributeFileSystem();

            Result rc = fs.GetFileSize(out _, "/path".ToU8Span());

            Assert.Equal(ResultFs.PathNotFound.Value, rc);
        }

        [Fact]
        public void GetFileSize_PathIsDirectory_ReturnsPathNotFound()
        {
            IAttributeFileSystem fs = CreateAttributeFileSystem();
            fs.CreateDirectory("/dir".ToU8Span());

            Result rc = fs.GetFileSize(out _, "/dir".ToU8Span());

            Assert.Equal(ResultFs.PathNotFound.Value, rc);
        }
    }
}
