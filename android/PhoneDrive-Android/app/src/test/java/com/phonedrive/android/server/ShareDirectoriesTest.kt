package com.phonedrive.android.server

import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

class ShareDirectoriesTest {
    @get:Rule val temp = TemporaryFolder()

    @Test fun createsUnicodeFolderInsideSelectedParent() {
        val base = temp.newFolder("storage")
        val parent = File(base, "Download").apply { mkdir() }
        val result = ShareDirectories.create(base, parent, "我的共享目录")
        assertTrue(result.isDirectory)
        assertEquals(parent.canonicalFile, result.canonicalFile.parentFile)
    }

    @Test fun rejectsTraversalAndInvalidNames() {
        val base = temp.newFolder("storage")
        for (name in listOf("..", ".", "../outside", "one/two", "one\\two", "", " ", " trailing ", "bad\u0000name"))
            assertThrows(IllegalArgumentException::class.java) { ShareDirectories.create(base, base, name) }
        assertEquals(0, base.listFiles()!!.size)
    }

    @Test fun existingFileAndFolderArePreserved() {
        val base = temp.newFolder("storage")
        File(base, "file").writeText("keep")
        File(base, "folder").mkdir()
        for (name in listOf("file", "folder"))
            assertThrows(IllegalArgumentException::class.java) { ShareDirectories.create(base, base, name) }
        assertEquals("keep", File(base, "file").readText())
    }

    @Test fun rejectsOutsideAndRestrictedDirectoriesButAllowsSimilarNames() {
        val base = temp.newFolder("storage")
        val outside = temp.newFolder("storage-other")
        assertThrows(IllegalArgumentException::class.java) { ShareDirectories.validate(base, outside) }
        for (name in listOf("Android/data", "Android/data/child", "Android/obb")) {
            val folder = File(base, name).apply { mkdirs() }
            assertThrows(IllegalArgumentException::class.java) { ShareDirectories.validate(base, folder) }
        }
        val allowed = File(base, "Android/database").apply { mkdirs() }
        assertEquals(allowed.canonicalFile, ShareDirectories.validate(base, allowed))
        assertEquals(base.canonicalFile, ShareDirectories.validate(base, base))
    }
}
