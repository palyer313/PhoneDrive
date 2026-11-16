package com.phonedrive.android.transfer

import com.phonedrive.android.filemanager.FileEntry
import com.phonedrive.android.filemanager.FileProvider
import com.phonedrive.android.filemanager.ProviderCapabilities
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream

class TwoPhaseSourceDeleterTest {

    @Test
    fun delete_success_removesAllSourcesWithoutLeavingStagingNames() {
        val provider = FakeProvider("/a.txt", "/b.txt")

        TwoPhaseSourceDeleter.delete(provider, listOf("/a.txt", "/b.txt"), "task") {}

        assertTrue(provider.paths.isEmpty())
    }

    @Test
    fun deleteFailure_reportsPartialCountAndRestoresUndeletedSources() {
        val provider = FakeProvider("/a.txt", "/b.txt").apply {
            failDelete = "/.phonedrive-delete-task-1"
        }

        val error = runCatching {
            TwoPhaseSourceDeleter.delete(provider, listOf("/a.txt", "/b.txt"), "task") {}
        }.exceptionOrNull()

        assertTrue(error is SourceDeletionException)
        error as SourceDeletionException
        assertEquals(1, error.deletedCount)
        assertEquals(1, error.remainingCount)
        assertFalse(provider.exists("/a.txt"))
        assertTrue(provider.exists("/b.txt"))
        assertFalse(provider.exists("/.phonedrive-delete-task-1"))
    }

    @Test
    fun stagingFailure_restoresFilesAlreadyRenamed() {
        val provider = FakeProvider("/a.txt", "/b.txt").apply {
            failRenameFrom = "/b.txt"
        }

        val error = runCatching {
            TwoPhaseSourceDeleter.delete(provider, listOf("/a.txt", "/b.txt"), "task") {}
        }.exceptionOrNull()

        assertTrue(error is IOException)
        assertTrue(provider.exists("/a.txt"))
        assertTrue(provider.exists("/b.txt"))
        assertFalse(provider.exists("/.phonedrive-delete-task-0"))
    }

    private class FakeProvider(vararg initial: String) : FileProvider {
        val paths = initial.toMutableSet()
        var failDelete: String? = null
        var failRenameFrom: String? = null

        override val id: String = "fake"
        override val displayName: String = "fake"

        override fun list(path: String): List<FileEntry> = emptyList()

        override fun stat(path: String): FileEntry {
            if (path !in paths) throw IllegalArgumentException("文件不存在：$path")
            return FileEntry(
                name = path.substringAfterLast('/'),
                path = path,
                size = 0,
                modifiedAt = 0,
                isDirectory = false,
            )
        }

        override fun openRead(path: String, offset: Long): InputStream = ByteArrayInputStream(ByteArray(0))
        override fun openWrite(path: String, append: Boolean): OutputStream = ByteArrayOutputStream()
        override fun mkdir(path: String): Boolean = paths.add(path)

        override fun delete(path: String): Boolean {
            if (path == failDelete) return false
            return paths.remove(path)
        }

        override fun deleteEmptyDirectory(path: String): Boolean = paths.remove(path)

        override fun rename(from: String, to: String): Boolean {
            if (from == failRenameFrom || from !in paths || to in paths) return false
            paths.remove(from)
            paths.add(to)
            return true
        }

        override fun exists(path: String): Boolean = path in paths
        override fun search(keyword: String, scope: String): List<FileEntry> = emptyList()
        override fun capabilities(): ProviderCapabilities = ProviderCapabilities()
        override fun localFile(path: String): File? = null
    }
}
