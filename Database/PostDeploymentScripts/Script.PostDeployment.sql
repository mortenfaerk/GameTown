PRINT 'Running Post-Deployment Script...';

INSERT INTO [dbo].GameTownRoles ([Role],[CreatedBy],[CreatedDate],[ModifiedBy],[ModifiedDate],[IsActive])
VALUES ('Admin','System',GETDATE(),'System',GETDATE(),1),
       ('Contributor','System',GETDATE(),'System',GETDATE(),1)