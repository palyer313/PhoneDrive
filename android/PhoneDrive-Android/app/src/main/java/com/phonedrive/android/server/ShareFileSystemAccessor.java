package com.phonedrive.android.server;

import org.apache.sshd.sftp.server.*;
import org.apache.sshd.server.session.ServerSession;
import java.io.IOException;
import java.nio.channels.SeekableByteChannel;
import java.nio.file.*;
import java.nio.file.attribute.*;
import java.security.Principal;
import java.util.*;
import java.util.function.Predicate;

/** Enforces read-only before opening/truncating files, not after a write has already happened. */
public final class ShareFileSystemAccessor implements SftpFileSystemAccessor {
    private final Predicate<ServerSession> readOnly;
    public ShareFileSystemAccessor(Predicate<ServerSession> readOnly) { this.readOnly = readOnly; }
    private void writable(SftpSubsystemProxy subsystem) throws IOException {
        if (readOnly.test(subsystem.getServerSession())) throw new AccessDeniedException("共享目录为只读");
    }
    @Override public SeekableByteChannel openFile(SftpSubsystemProxy s, FileHandle h, Path p, String id,
        Set<? extends OpenOption> options, FileAttribute<?>... attrs) throws IOException {
        if (options.stream().anyMatch(o -> o != StandardOpenOption.READ && o != LinkOption.NOFOLLOW_LINKS)) writable(s);
        return SftpFileSystemAccessor.super.openFile(s,h,p,id,options,attrs);
    }
    @Override public void createDirectory(SftpSubsystemProxy s, Path p) throws IOException { writable(s); SftpFileSystemAccessor.super.createDirectory(s,p); }
    @Override public void removeFile(SftpSubsystemProxy s, Path p, boolean dir) throws IOException { writable(s); SftpFileSystemAccessor.super.removeFile(s,p,dir); }
    @Override public void renameFile(SftpSubsystemProxy s, Path a, Path b, Collection<CopyOption> opts) throws IOException { writable(s); SftpFileSystemAccessor.super.renameFile(s,a,b,opts); }
    @Override public void copyFile(SftpSubsystemProxy s, Path a, Path b, Collection<CopyOption> opts) throws IOException { writable(s); SftpFileSystemAccessor.super.copyFile(s,a,b,opts); }
    @Override public void createLink(SftpSubsystemProxy s, Path a, Path b, boolean symbolic) throws IOException { writable(s); throw new AccessDeniedException("不允许创建可能绕过共享边界的链接"); }
    @Override public void setFileAttribute(SftpSubsystemProxy s, Path p, String view, String attr, Object value, LinkOption... opts) throws IOException { writable(s); SftpFileSystemAccessor.super.setFileAttribute(s,p,view,attr,value,opts); }
    @Override public void setFileOwner(SftpSubsystemProxy s, Path p, Principal owner, LinkOption... opts) throws IOException { writable(s); SftpFileSystemAccessor.super.setFileOwner(s,p,owner,opts); }
    @Override public void setGroupOwner(SftpSubsystemProxy s, Path p, Principal owner, LinkOption... opts) throws IOException { writable(s); SftpFileSystemAccessor.super.setGroupOwner(s,p,owner,opts); }
    @Override public void setFilePermissions(SftpSubsystemProxy s, Path p, Set<PosixFilePermission> permissions, LinkOption... opts) throws IOException { writable(s); SftpFileSystemAccessor.super.setFilePermissions(s,p,permissions,opts); }
    @Override public void setFileAccessControl(SftpSubsystemProxy s, Path p, List<AclEntry> acl, LinkOption... opts) throws IOException { writable(s); SftpFileSystemAccessor.super.setFileAccessControl(s,p,acl,opts); }
}
