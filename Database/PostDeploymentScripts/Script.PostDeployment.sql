PRINT 'Running Post-Deployment Script...';

INSERT INTO [dbo].GameTownRoles ([Id],[Role],[CreatedBy],[CreatedDate],[ModifiedBy],[ModifiedDate],[IsActive])
VALUES ('99ffbcba-6c26-416f-b996-33e8a0b4c6ef','Admin','System',GETDATE(),'System',GETDATE(),1),
       ('37a3c94f-b2e0-46ac-a60b-2b9eb09c3a14','Contributor','System',GETDATE(),'System',GETDATE(),1)


    PRINT 'Inserting default user for DEV environment...';

    INSERT INTO [dbo].GameTownUsers ([Id],[PasswordHash],[Salt],[Username],[DisplayName],[IsActive],[Notes],[CreatedAt],[CreatedBy],[LastModifiedAt],[LastModifiedBy])
    VALUES ('8f50b277-0b2d-4245-b686-e9c77a32b966','C59A7F1470254A8ABFD25CD44192EAA90A5CF41640B2530A31420A8399A83693','A05611E9D30B3ED4D7A9A370A608ED98','test','Test User',1,'Default user for development environment',GETDATE(),'System',GETDATE(),'System')

    INSERT INTO [dbo].GameTownUsers_Roles (APIUserId,APIRoleId)
    VALUES('8f50b277-0b2d-4245-b686-e9c77a32b966','99ffbcba-6c26-416f-b996-33e8a0b4c6ef')