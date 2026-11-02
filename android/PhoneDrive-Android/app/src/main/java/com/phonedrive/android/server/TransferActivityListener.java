package com.phonedrive.android.server;

import org.apache.sshd.server.session.ServerSession;
import org.apache.sshd.sftp.server.FileHandle;
import org.apache.sshd.sftp.server.SftpEventListener;

/** Observe real file I/O without replacing FileChannel or changing fsync/locking behavior. */
public final class TransferActivityListener implements SftpEventListener {
    @Override public void reading(ServerSession s, String id, FileHandle h, long offset, byte[] data, int start, int len) {
        SftpServerService.noteTransferActivity();
    }
    @Override public void writing(ServerSession s, String id, FileHandle h, long offset, byte[] data, int start, int len) {
        SftpServerService.noteTransferActivity();
    }
}
